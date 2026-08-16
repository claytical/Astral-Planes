using UnityEngine;

/// <summary>
/// Ground-plane radius ring for the discard-blast countdown. Unlike the bomb's own
/// body/particle swell (a fixed cosmetic multiplier, unrelated to the true blast size until the
/// post-detonation flash), this ring is sized to the real, resolved blast radius for the entire
/// fuse — a player can read the actual danger zone from the moment the bomb arms, not guess from
/// how big the sprite looks. Built at runtime, same pattern as VehicleReleaseCue.BuildCircle.
/// </summary>
public partial class Collectable
{
    [Header("Discard Blast Telegraph")]
    [Tooltip("Ring/annulus sprite for the blast-radius telegraph. Unassigned = the ring renders nothing (no fallback shape) — assign a hollow ring sprite in the Inspector for the visual to appear.")]
    [SerializeField] private Sprite bombTelegraphRingSprite;

    private SpriteRenderer _bombTelegraphRing;

    private void EnsureBombTelegraphRing()
    {
        if (_bombTelegraphRing != null) return;

        var go = new GameObject("BombBlastTelegraphRing");
        // Sibling of bombVisualRoot (not a child of it) — the ring's own scale already equals
        // the true world-space diameter, so it must not additionally inherit the cosmetic swell.
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        _bombTelegraphRing = go.AddComponent<SpriteRenderer>();
        _bombTelegraphRing.sprite = bombTelegraphRingSprite;
        if (energySprite != null)
        {
            _bombTelegraphRing.sortingLayerName = energySprite.sortingLayerName;
            _bombTelegraphRing.sortingOrder = energySprite.sortingOrder - 1;
        }
        _bombTelegraphRing.color = new Color(1f, 1f, 1f, 0f);
        _bombTelegraphRing.enabled = false;
    }

    /// <summary>t is fuse progress 0→1, same value BombFuseRoutine already computes per frame.</summary>
    private void UpdateBombTelegraphRing(float t)
    {
        if (_bombTelegraphRing == null) return;

        float pulseHz = tuning.bomb.telegraphPulseHz;
        float pulse = (Mathf.Sin(Time.time * pulseHz * Mathf.PI * 2f) * 0.5f) + 0.5f;
        // Pulse only ever shrinks below the true radius (max multiplier is exactly 1), so the
        // ring never overstates the real danger zone.
        float scaleMul = Mathf.Lerp(tuning.bomb.telegraphPulseMin, 1f, pulse);
        float diameter = _bombEffectiveBlastRadius * 2f * scaleMul;
        _bombTelegraphRing.transform.localScale = Vector3.one * diameter;

        var c = tuning.bomb.warningColor;
        c.a = Mathf.Lerp(0f, tuning.bomb.telegraphMaxAlpha, t);
        _bombTelegraphRing.color = c;
        _bombTelegraphRing.enabled = true;
    }

    private void HideBombTelegraphRing()
    {
        if (_bombTelegraphRing != null) _bombTelegraphRing.enabled = false;
    }
}
