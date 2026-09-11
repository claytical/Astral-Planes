using UnityEngine;
using System.Collections;

public partial class Vehicle
{
    public int GetForceAsDamage()
        {
            float speed = rb.linearVelocity.magnitude;
            float impactCapVelocity = profile != null ? profile.impactSpeedCap : 32f;
            float exponent = profile != null ? profile.damageCurveExponent : 1.75f;
            float minDmg = profile != null ? profile.minImpactDamage : 25f;
            float maxDmg = profile != null ? profile.maxImpactDamage : 100f;
            float massMulMin = profile != null ? profile.massDamageMultiplierMin : 0.75f;
            float massMulMax = profile != null ? profile.massDamageMultiplierMax : 2f;
            float followUpWindow = profile != null ? profile.followUpDamageWindowSeconds : 0.5f;
            float followUpFloor = profile != null ? profile.followUpDamageFloor : 10f;
            float ceiling = profile != null ? profile.damageCeiling : 120f;

            float normalizedSpeed = Mathf.InverseLerp(0f, impactCapVelocity, speed);
            float curvedSpeed = Mathf.Pow(normalizedSpeed, exponent);
            float baseDamage = Mathf.Lerp(minDmg, maxDmg, curvedSpeed);

            float massMultiplier = Mathf.Clamp(rb.mass, massMulMin, massMulMax);
            float damage = baseDamage * massMultiplier;

            // If we hit something within the last followUpWindow seconds, pad the damage slightly
            if (Time.time - _lastDamageTime < followUpWindow)
            {
                damage = Mathf.Max(damage, followUpFloor); // Floor for quick follow-ups
            }

            _lastDamageTime = Time.time;

            return Mathf.RoundToInt(Mathf.Clamp(damage, 0f, ceiling));
        }
    public float HitVelocityMultiplier => profile != null ? profile.hitVelocityMultiplier : 1.0f;

    public float GetForceAsMidiVelocity()
    {
        float speed = rb.linearVelocity.magnitude;

        // Make sure this reflects *true* achievable speed (including boost),
        // otherwise you will peg at 127 constantly.
        float max = Mathf.Max(0.01f, arcadeMaxSpeed);

        float x = Mathf.Clamp01(speed / max);

        // Optional: curve to give more resolution in the midrange
        // x = Mathf.Pow(x, 0.7f);

        return Mathf.Lerp(40f, 127f, x);
    }

    void OnCollisionEnter2D(Collision2D coll)
    {
        var node = coll.gameObject.GetComponent<DiscoveryTrackNode>();

        // 🎯 Apply impact damage
        int damage = GetForceAsDamage();
        if (node != null)
        {
            TriggerFlickerAndPulse(1.2f, node.coreSprite.color, false);
            // 💥 Apply knockback
            Rigidbody2D nodeRb = node.GetComponent<Rigidbody2D>();
            if (nodeRb != null)
            {
                Vector2 forceDirection = rb.linearVelocity.normalized;
                float knockbackForce = rb.mass * rb.linearVelocity.magnitude * 0.5f; // Tunable
                nodeRb.AddForce(forceDirection * knockbackForce, ForceMode2D.Impulse);
            }
        }

        if (coll.gameObject.tag == "Bump")
        {
            TriggerThud(coll.contacts[0].point);
        }

        var ringApplicator = coll.gameObject.GetComponent<MotifRingGlyphApplicator>();
        if (ringApplicator != null)
        {
            if (!ringApplicator.TryBreakNextPendingTether(coll.contacts[0].point))
                ringApplicator.PlayDenialShake();
        }
}

    // Called right after this vehicle commits a manually-released note (Vehicle.ManualRelease.cs).
    // Depositing a note leaves the vehicle sitting at the ring, so the very next physics tick's
    // OnCollisionEnter2D above would otherwise almost always fire on the ring the vehicle just
    // fed — breaking the note it just committed via TryBreakNextPendingTether. A brief, one-shot
    // collision-ignore between this vehicle and the ring collider avoids that guaranteed self-hit
    // without touching the break-on-collision hazard for any other collision.
    private void GrantPostCommitRingCollisionImmunity()
    {
        if (gfm == null) gfm = GameFlowManager.Instance;
        var ringApplicator = gfm?.GetMotifRingGlyphApplicator();
        var ringCollider = ringApplicator != null ? ringApplicator.RingCollider : null;
        if (ringCollider == null) return;

        var vehicleCollider = GetComponent<Collider2D>();
        if (vehicleCollider == null) return;

        float duration = vehicleConfig.postCommitRingCollisionImmunitySeconds;
        if (duration <= 0f) return;

        Physics2D.IgnoreCollision(vehicleCollider, ringCollider, true);
        StartCoroutine(RestoreRingCollisionAfterDelay(vehicleCollider, ringCollider, duration));
    }

    private System.Collections.IEnumerator RestoreRingCollisionAfterDelay(Collider2D vehicleCollider, Collider2D ringCollider, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (vehicleCollider != null && ringCollider != null)
            Physics2D.IgnoreCollision(vehicleCollider, ringCollider, false);
    }

    private void TriggerThud(Vector2 collisionPoint)
        {
            if (baseSprite == null || isFlickering) return;

            if (flickerPulseRoutine != null)
            {
                StopCoroutine(flickerPulseRoutine); // Prevent stacking
            }
            flickerPulseRoutine = StartCoroutine(ThudRoutine(collisionPoint));
        }
    private void TriggerFlickerAndPulse(float scaleMultiplier, Color? baseColor = null, bool cycleHue = false, float duration = 0.2f)
        {
            if (baseSprite == null || isFlickering) return;

            if (flickerPulseRoutine != null)
            {
                StopCoroutine(flickerPulseRoutine); // Prevent stacking
            }

            flickerPulseRoutine = StartCoroutine(FlickerAndPulseRoutine(scaleMultiplier, baseColor, cycleHue, duration));
        }

    /// <summary>
    /// Momentary "hit" reaction for an energy-draining blast (e.g. a discarded-note bomb):
    /// flashes to the color that hit it, shrinks, then springs back to normal size/color.
    /// SpectrumFlickerWithPulse's pulse curve is symmetric around 1, so a sub-1 scaleMultiplier
    /// (drainFeedbackShrinkScale) produces a shrink-and-recover for free — same routine the
    /// DiscoveryTrackNode bump reaction above uses with a >1 (grow) value.
    /// </summary>
    public void TriggerDrainFeedback(Color hitColor) =>
        TriggerFlickerAndPulse(vehicleConfig.drainFeedbackShrinkScale, hitColor, false, vehicleConfig.drainFeedbackDuration);

    /// <summary>
    /// Instantaneous knockback impulse (e.g. a discarded-note bomb). ForceMode2D.Impulse (not a
    /// direct linearVelocity write) so heavier ships resist more via rb.mass, matching the
    /// Vehicle→DiscoveryTrackNode knockback above. UpdateMovementAndFuel only ever steers
    /// linearVelocity toward desiredVel at a clamped accel rate — it never overwrites it outright —
    /// so the impulse bleeds off naturally over several ticks instead of vanishing next frame.
    /// </summary>
    public void ApplyKnockback(Vector2 impulseWorld)
    {
        if (rb == null) return;
        rb.AddForce(impulseWorld, ForceMode2D.Impulse);
    }
    private IEnumerator ThudRoutine(Vector2 coll)
        {
            isFlickering = true;
            yield return VisualFeedbackUtility.BoundaryThudFeedback(baseSprite, transform, coll);
            isFlickering = false;
            flickerPulseRoutine = null;
        }
    private IEnumerator FlickerAndPulseRoutine(float scaleMultiplier, Color? baseColor, bool cycleHue, float duration = 0.2f)
        {
            isFlickering = true;

            yield return VisualFeedbackUtility.SpectrumFlickerWithPulse(
                baseSprite,
                transform,
                duration,
                scaleMultiplier,
                cycleHue ? null : baseColor,
                cycleHue
            );

            isFlickering = false;
            flickerPulseRoutine = null;
        }
}
