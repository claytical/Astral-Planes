using UnityEngine;

// Fuel status tells on the ship itself (LEGIBILITY_AUDIT.md D2): a soft glow drawn behind the
// hull on its own runtime child sprite, so it never fights baseSprite.color (flicker, ring
// phase ghosting and note-trail tinting all write that).
//
//   Refueling in a gravity void → steady cool glow with a gentle shimmer (was a silent trickle).
//   Low fuel (< lowFuelThreshold01) → warm warning glow that pulses faster as fuel nears zero.
//
// Refueling wins while both apply — it's the "you're fixing it" answer to the warning.
// The fuel bar mirrors both states (Fuel.cs), fed via PlayerStats.SetFuelRefueling.
public partial class Vehicle
{
    private const float kStatusGlowFadeSpeed = 6f;
    private const float kRefuelShimmerHz = 2f;

    private static Sprite s_statusGlowSprite;

    private SpriteRenderer _statusGlow;
    private float _statusGlowAlpha;
    private bool _voidRefueling;

    private void SetVoidRefueling(bool refueling)
    {
        if (_voidRefueling == refueling) return;
        _voidRefueling = refueling;
        if (playerStatsUI != null) playerStatsUI.SetFuelRefueling(refueling);
    }

    private void TickStatusGlow(float dt)
    {
        if (vehicleConfig == null) return;

        bool visible = !_isDead && baseSprite != null && baseSprite.enabled;
        float ratio = capacity > 0f ? energyLevel / capacity : 1f;
        bool low = visible && ratio > 0f && ratio < vehicleConfig.lowFuelThreshold01;

        Color color = Color.white;
        float targetAlpha = 0f;
        float maxA = vehicleConfig.statusGlowMaxAlpha;

        if (visible && _voidRefueling)
        {
            float p = 0.5f + 0.5f * Mathf.Sin(Time.time * kRefuelShimmerHz * Mathf.PI * 2f);
            color = vehicleConfig.voidRefuelGlowColor;
            targetAlpha = maxA * (0.6f + 0.4f * p);
        }
        else if (low)
        {
            float urgency = 1f - ratio / Mathf.Max(0.0001f, vehicleConfig.lowFuelThreshold01);
            float hz = Mathf.Lerp(vehicleConfig.lowFuelPulseHz, vehicleConfig.lowFuelPulseHzEmpty, urgency);
            float p = 0.5f + 0.5f * Mathf.Sin(Time.time * hz * Mathf.PI * 2f);
            color = vehicleConfig.lowFuelGlowColor;
            targetAlpha = maxA * p;
        }

        // A dead ship never ticks UpdateVoidEnergyRecovery (FixedUpdate early-outs), so clear
        // the refuel state here or the fuel bar would keep shimmering.
        if (_isDead && _voidRefueling) SetVoidRefueling(false);

        // While a state is active, track its (already smooth) pulse closely; ease out when off.
        bool active = visible && (low || _voidRefueling);
        float speed = active ? kStatusGlowFadeSpeed * 4f : kStatusGlowFadeSpeed;
        _statusGlowAlpha = Mathf.Lerp(_statusGlowAlpha, targetAlpha, 1f - Mathf.Exp(-speed * dt));

        if (_statusGlowAlpha < 0.003f)
        {
            if (_statusGlow != null && _statusGlow.enabled) _statusGlow.enabled = false;
            return;
        }

        EnsureStatusGlow();
        if (_statusGlow == null) return;
        _statusGlow.enabled = true;
        color.a = _statusGlowAlpha;
        _statusGlow.color = color;

        // Sized from the ship's own sprite (not collider/particles), centered on the note core.
        _statusGlow.transform.position = NoteCoreAnchor.position;
        _statusGlow.transform.rotation = Quaternion.identity;
        Vector3 ls = transform.lossyScale;
        float d = GetShipSpriteRadiusWorld() * vehicleConfig.statusGlowRadiusMul * 2f;
        _statusGlow.transform.localScale = new Vector3(
            Mathf.Abs(ls.x) > 0.0001f ? d / ls.x : d,
            Mathf.Abs(ls.y) > 0.0001f ? d / ls.y : d, 1f);
    }

    private void EnsureStatusGlow()
    {
        if (_statusGlow != null) return;
        var go = new GameObject("Fuel Status Glow");
        go.transform.SetParent(transform, false);
        _statusGlow = go.AddComponent<SpriteRenderer>();
        _statusGlow.sprite = GetStatusGlowSprite();
        if (baseSprite != null)
        {
            _statusGlow.sortingLayerID = baseSprite.sortingLayerID;
            _statusGlow.sortingOrder = baseSprite.sortingOrder - 1; // behind the hull
        }
    }

    // Procedural radial falloff (bright center → transparent rim), 1 world unit across at scale 1.
    private static Sprite GetStatusGlowSprite()
    {
        if (s_statusGlowSprite != null) return s_statusGlowSprite;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        float r = n * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
            float a = Mathf.Clamp01(1f - d);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
        }
        tex.Apply();
        s_statusGlowSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return s_statusGlowSprite;
    }
}
