using UnityEngine;

/// <summary>
/// Ground-plane radius ring + interior filled overlay for the discard-blast countdown. Unlike
/// the bomb's own body/particle swell (a fixed cosmetic multiplier, unrelated to the true blast
/// size until the post-detonation flash), both of these are sized to the real, resolved blast
/// radius for the entire fuse — a player can read the actual danger zone from the moment the
/// bomb arms, not guess from how big the sprite looks. Built at runtime, same pattern as
/// VehicleReleaseCue.BuildCircle. The ring stays invisible until urgency rises (danger-window
/// escalation); the overlay is visible at low alpha immediately, so the footprint itself is
/// always readable, while the ring's rising red intensity communicates "how soon."
/// </summary>
public partial class Collectable
{
    [Header("Discard Blast Telegraph")]
    [Tooltip("Ring/annulus sprite for the blast-radius telegraph. Unassigned = the ring renders nothing (no fallback shape) — assign a hollow ring sprite in the Inspector for the visual to appear.")]
    [SerializeField] private Sprite bombTelegraphRingSprite;

    [Tooltip("Filled-circle sprite for the interior blast-footprint overlay (behind the ring/body). Unassigned = renders nothing — assign a filled circle sprite in the Inspector, same convention as bombTelegraphRingSprite.")]
    [SerializeField] private Sprite bombBlastOverlaySprite;

    private SpriteRenderer _bombTelegraphRing;
    private SpriteRenderer _bombBlastOverlay;

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

    /// <summary>
    /// urgency01: 0 while more than tuning.bomb.dangerWindowSeconds remain before detonation,
    /// ramping to 1 at detonation. NOT full-fuse progress — that's `t` in BombFuseRoutine, which
    /// still separately drives pulse Hz and swell scale, unaffected by this. Both color AND alpha
    /// lerp off urgency01 now (previously the ring was always warningColor, only alpha ramped).
    /// </summary>
    private void UpdateBombTelegraphRing(float urgency01)
    {
        if (_bombTelegraphRing == null) return;

        float pulseHz = tuning.bomb.telegraphPulseHz;
        float pulse = (Mathf.Sin(Time.time * pulseHz * Mathf.PI * 2f) * 0.5f) + 0.5f;
        // Pulse only ever shrinks below the true radius (max multiplier is exactly 1), so the
        // ring never overstates the real danger zone.
        float scaleMul = Mathf.Lerp(tuning.bomb.telegraphPulseMin, 1f, pulse);
        float diameter = _bombEffectiveBlastRadius * 2f * scaleMul;
        _bombTelegraphRing.transform.localScale = Vector3.one * diameter;

        var c = Color.Lerp(tuning.bomb.calmColor, tuning.bomb.warningColor, urgency01);
        c.a = Mathf.Lerp(0f, tuning.bomb.telegraphMaxAlpha, urgency01);
        _bombTelegraphRing.color = c;
        _bombTelegraphRing.enabled = true;
    }

    private void HideBombTelegraphRing()
    {
        if (_bombTelegraphRing != null) _bombTelegraphRing.enabled = false;
    }

    private void EnsureBombBlastOverlay()
    {
        if (_bombBlastOverlay != null) return;

        var go = new GameObject("BombBlastOverlay");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        _bombBlastOverlay = go.AddComponent<SpriteRenderer>();
        _bombBlastOverlay.sprite = bombBlastOverlaySprite;
        if (energySprite != null)
        {
            _bombBlastOverlay.sortingLayerName = energySprite.sortingLayerName;
            // One layer further behind than the ring (energySprite.sortingOrder - 1), so
            // stacking is body (0) > ring (-1) > overlay (-2) — the filled footprint never
            // occludes either.
            _bombBlastOverlay.sortingOrder = energySprite.sortingOrder - 2;
        }
        _bombBlastOverlay.color = new Color(1f, 1f, 1f, 0f);
        _bombBlastOverlay.enabled = false;
    }

    /// <summary>
    /// Fixed at the true blast diameter throughout the fuse (no pulse, unlike the ring) — this is
    /// the "here's exactly where it'll hit" read, visible at low alpha from the moment of arming.
    /// urgency01 escalates only its intensity (color + alpha), same driver as UpdateBombTelegraphRing.
    /// </summary>
    private void UpdateBombBlastOverlay(float urgency01)
    {
        if (_bombBlastOverlay == null) return;

        float diameter = _bombEffectiveBlastRadius * 2f;
        _bombBlastOverlay.transform.localScale = Vector3.one * diameter;

        var c = Color.Lerp(tuning.bomb.calmColor, tuning.bomb.warningColor, urgency01);
        c.a = Mathf.Lerp(tuning.bomb.overlayMinAlpha, tuning.bomb.overlayMaxAlpha, urgency01);
        _bombBlastOverlay.color = c;
        _bombBlastOverlay.enabled = true;
    }

    private void HideBombBlastOverlay()
    {
        if (_bombBlastOverlay != null) _bombBlastOverlay.enabled = false;
    }
}
