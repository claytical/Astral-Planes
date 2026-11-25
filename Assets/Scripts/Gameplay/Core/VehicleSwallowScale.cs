using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Remembers a vehicle's authored scale so the path-exit transition can shrink it into a black
/// hole and grow it back out of the next one, then restore it exactly. Scale only — it never
/// touches sprite colors (the damage flicker owns those).
/// </summary>
public static class VehicleSwallowScale
{
    /// <summary>Scale factor at which a swallowed vehicle is effectively invisible.</summary>
    public const float Swallowed = 0.02f;

    private static readonly Dictionary<Vehicle, Vector3> s_original = new();

    /// <summary>Scales the vehicle to <paramref name="factor"/> of its authored scale (captured on first use).</summary>
    public static void Apply(Vehicle v, float factor)
    {
        if (v == null) return;
        if (!s_original.TryGetValue(v, out var orig))
        {
            // The authored scale, not the live one: a wall-thud squash still easing back when the
            // exit grabs the ship would otherwise be "restored" as its new normal (squashed ship).
            orig = v.BaseLocalScale;
            s_original[v] = orig;
        }
        v.transform.localScale = orig * Mathf.Max(Swallowed, factor);
    }

    /// <summary>Restores the authored scale and forgets it. No-op if never scaled.</summary>
    public static void Restore(Vehicle v)
    {
        if (v == null) return;
        if (s_original.TryGetValue(v, out var orig))
        {
            v.transform.localScale = orig;
            s_original.Remove(v);
        }
    }

    public static bool IsScaled(Vehicle v) => v != null && s_original.ContainsKey(v);
}
