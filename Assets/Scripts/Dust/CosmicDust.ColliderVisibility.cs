using UnityEngine;

public partial class CosmicDust
{
    public void SetTerrainColliderEnabled(bool enabled)
    {
        // Prefer cached colliders so we reliably toggle whatever collider is actually producing contact.
        if (_cachedColliders == null || _cachedColliders.Length == 0)
            _cachedColliders = GetComponentsInChildren<Collider2D>(true);

        // Determine current effective state (defensive: prefab defaults can drift from our expectations).
        bool currentlyEnabled = false;
        if (_cachedColliders != null)
        {
            for (int i = 0; i < _cachedColliders.Length; i++)
            {
                var c = _cachedColliders[i];
                if (c != null && c.enabled) { currentlyEnabled = true; break; }
            }
        }
        if (terrainCollider != null && terrainCollider.enabled)
            currentlyEnabled = true;

        // If enabling, always enforce visual invariants even when collider state already matches.
        if (enabled)
            EnsureVisibleWhenCollidable();

        // If already in the desired state, do nothing after visual consistency check above.
        if (currentlyEnabled == enabled)
            return;

        if (_cachedColliders != null)
        {
            for (int i = 0; i < _cachedColliders.Length; i++)
            {
                var c = _cachedColliders[i];
                if (c != null) c.enabled = enabled;
            }
        }

        // Maintain legacy field behavior too.
        if (terrainCollider != null) terrainCollider.enabled = enabled;

        // Mirror collider state onto sprite alpha so non-interactive cells read as ghost/faint.
        if (visual.sprite != null)
        {
            Color c = _displayTint;
            if (enabled)
            {
                // Restore authoritative tint alpha — do not cap at kRegrowAlphaCap here,
                // which would cause a visible brightness drop on cells that are already at
                // proper alpha when their collider is re-enabled after spawn or regrowth.
                c.a = _currentTint.a;
            }
            else
            {
                c.a = colliderDisabledAlpha;
            }
            ApplyDisplayedTint(c);
        }

        // When enabling collision, ensure CircleCollider2D radius matches the current sprite footprint.
        if (enabled)
        {
            RebuildColliderForCurrentScale();
            SyncColliderRadiusToSprite();
        }

        OnCollisionStateChanged?.Invoke(enabled);
    }
    public bool IsVisuallyPresentForTargeting(float minEffectiveAlpha = 0.05f, float minAbsScale = 0.01f)
    {
        if (visual.sprite == null || !visual.sprite.enabled)
            return false;

        var srColor = visual.sprite.color;
        float effectiveAlpha = srColor.a;
        if (effectiveAlpha <= minEffectiveAlpha)
            return false;

        Vector3 lossy = visual.sprite.transform.lossyScale;
        if (Mathf.Abs(lossy.x) <= minAbsScale || Mathf.Abs(lossy.y) <= minAbsScale)
            return false;

        return true;
    }
    private void EnsureVisibleWhenCollidable()
    {
        // Invariant: active collider → visible sprite + particle renderers.
        // HideVisualsInstant() can disable these; re-enable them so the cell is never
        // an invisible wall with an active collider.
        SetVisualsEnabled(true);

        if (visual.particleSystem != null)
        {
            var systems = _particles.GetAllParticleSystems();
            if (systems != null)
                for (int i = 0; i < systems.Length; i++)
                {
                    if (systems[i] == null) continue;
                    var pr = systems[i].GetComponent<ParticleSystemRenderer>();
                    if (pr != null) pr.enabled = true;
                }
        }

        // Defensive sanity: if a solid cell somehow has a zeroed sprite scale, restore expected target.
        if (visual.sprite != null && _spriteScaleTarget > 0f)
        {
            var ls = visual.sprite.transform.localScale;
            if (Mathf.Abs(ls.x) <= 0.0001f || Mathf.Abs(ls.y) <= 0.0001f)
                ResetSpriteScaleTo(_spriteScaleTarget);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (visual.sprite != null && !visual.sprite.enabled)
            Debug.LogWarning($"[{nameof(CosmicDust)}] Collider enabled while sprite renderer is disabled on '{name}'. Re-enabling visuals.", this);
#endif
    }
    private void SetVisualsEnabled(bool enabled)
{
    // In the simplified model, we only toggle sprite visibility.
    // Particles stay enabled; their "presence" is driven by emission rate (SetEmissionMultiplier).
    if (visual.sprite != null)
        visual.sprite.enabled = enabled;
}
}
