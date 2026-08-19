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

        // Mirror collider state onto displayed alpha so non-interactive cells read as ghost/faint.
        Color mirroredTint = _displayTint;
        if (enabled)
        {
            // Restore authoritative tint alpha — do not cap at kRegrowAlphaCap here,
            // which would cause a visible brightness drop on cells that are already at
            // proper alpha when their collider is re-enabled after spawn or regrowth.
            mirroredTint.a = _currentTint.a;
        }
        else
        {
            mirroredTint.a = colliderDisabledAlpha;
        }
        ApplyDisplayedTint(mirroredTint);

        // When enabling collision, ensure the box collider matches the current visual footprint.
        if (enabled)
        {
            RebuildColliderForCurrentScale();
        }

        OnCollisionStateChanged?.Invoke(enabled);
    }
    public bool IsVisuallyPresentForTargeting(float minEffectiveAlpha = 0.05f, float minAbsScale = 0.01f)
    {
        // Tentacles may only target cells the player has personally carved and are now
        // regrowing (vehicle-carve tell) — see CosmicDust.Flower.cs.
        if (!HasBloomedFlower)
            return false;

        if (visual.particleSystem == null)
            return false;

        var pr = visual.particleSystem.GetComponent<ParticleSystemRenderer>();
        if (pr == null || !pr.enabled)
            return false;

        // _currentTint.a is the authoritative charge/energy-driven alpha — this check has
        // always meant "is there enough charge left to be a legal target," not a render-timing
        // detail, so it keys off the same value ApplyDisplayedTint/particle color derive from.
        if (_currentTint.a <= minEffectiveAlpha)
            return false;

        Vector3 lossy = visual.particleSystem.transform.lossyScale;
        if (Mathf.Abs(lossy.x) <= minAbsScale || Mathf.Abs(lossy.y) <= minAbsScale)
            return false;

        return true;
    }
    private void EnsureVisibleWhenCollidable()
    {
        // Invariant: active collider → visible particle renderers.
        // HideVisualsInstant() can disable these; re-enable them so the cell is never
        // an invisible wall with an active collider.
        SetVisualsEnabled(true);

        // Defensive sanity: if a solid cell somehow has a zeroed visual scale, restore expected target.
        if (visual.particleSystem != null && _spriteScaleTarget > 0f)
        {
            var ls = visual.particleSystem.transform.localScale;
            if (Mathf.Abs(ls.x) <= 0.0001f || Mathf.Abs(ls.y) <= 0.0001f)
                ResetSpriteScaleTo(_spriteScaleTarget);
        }
    }
    private void SetVisualsEnabled(bool enabled)
{
    // Presence is driven by the particle renderer(s) directly; emission rate separately
    // drives density (SetEmissionMultiplier).
    if (visual.particleSystem == null) return;
    var systems = _particles.GetAllParticleSystems();
    if (systems == null) return;
    for (int i = 0; i < systems.Length; i++)
    {
        if (systems[i] == null) continue;
        var pr = systems[i].GetComponent<ParticleSystemRenderer>();
        if (pr != null) pr.enabled = enabled;
    }
}
}
