using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Remembers a vehicle's authored scale so the path-exit transition can shrink it into a black
/// hole and grow it back out of the next one, then restore it exactly. Scale only — it never
/// touches sprite colors (the damage flicker owns those).
/// </summary>
public static class VehicleSwallowScale
{
    private static readonly Dictionary<Vehicle, Vector3> s_original = new();

    /// <summary>Scales the vehicle to <paramref name="factor"/> of its authored scale (captured on first use).</summary>
    public static void Apply(Vehicle v, float factor)
    {
        if (v == null) return;
        if (!s_original.TryGetValue(v, out var orig))
        {
            orig = v.transform.localScale;
            s_original[v] = orig;
        }
        v.transform.localScale = orig * Mathf.Max(0.01f, factor);
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
