using System;
using System.Collections.Generic;
using UnityEngine;

public struct DustResistanceProfile
{
    public float carveResistance01;
    public float drainResistance01;
}

/// <summary>
/// Resolves the effective carve/drain resistance for a cell: live role profile takes
/// precedence over the cell's baked imprint values, which take precedence over defaults.
/// Also clamps and one-time-logs any out-of-range resistance data it encounters.
/// </summary>
public sealed class DustResistanceResolver
{
    private readonly DustImprintStore _imprints;
    private readonly Func<Vector2Int, CosmicDust> _tryGetDustAt;
    private readonly HashSet<string> _loggedInvalidResistanceContexts = new HashSet<string>();

    public DustResistanceResolver(DustImprintStore imprints, Func<Vector2Int, CosmicDust> tryGetDustAt)
    {
        _imprints = imprints;
        _tryGetDustAt = tryGetDustAt;
    }

    public DustResistanceProfile Resolve(Vector2Int cell, MusicalRole fallbackRole, string context)
    {
        // Determine the most authoritative role for this cell, from most to least specific:
        //   hidden imprint (pre-reveal true identity) > revealed imprint role > caller fallback
        MusicalRole role = fallbackRole;

        if (_imprints.TryGetValue(cell, out var imp) && imp.role != MusicalRole.None)
            role = imp.role;

        if (_imprints.TryGetValue(cell, out var hiddenImp2) && hiddenImp2.hiddenRole != MusicalRole.None)
            role = hiddenImp2.hiddenRole;

        // Live profile is always authoritative when a role is known.
        if (role != MusicalRole.None)
        {
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
            if (roleProfile != null)
                return Validate(new DustResistanceProfile
                {
                    carveResistance01 = roleProfile.carveResistance01,
                    drainResistance01 = roleProfile.drainResistance01
                }, $"{context}:live:{role}");
        }

        // Fallback: baked imprint values (None-role cells, trap cells with explicit overrides).
        if (_imprints.TryGetValue(cell, out var baked))
            return Validate(new DustResistanceProfile
            {
                carveResistance01 = baked.carveResistance01,
                drainResistance01 = baked.drainResistance01
            }, $"{context}:baked:{cell.x},{cell.y}");

        return Validate(new DustResistanceProfile(), $"{context}:default");
    }

    private DustResistanceProfile Validate(DustResistanceProfile profile, string context)
    {
        float carve = Mathf.Clamp01(profile.carveResistance01);
        float drain = Mathf.Clamp01(profile.drainResistance01);
        if ((carve != profile.carveResistance01 || drain != profile.drainResistance01)
            && _loggedInvalidResistanceContexts.Add(context))
        {
            Debug.LogWarning($"[DustResistance] Clamped invalid resistance data at {context}. carve={profile.carveResistance01:F3}->{carve:F3}, drain={profile.drainResistance01:F3}->{drain:F3}");
        }
        profile.carveResistance01 = carve;
        profile.drainResistance01 = drain;
        return profile;
    }

    public float GetLiveCarveResistance01(Vector2Int cell)
    {
        var dust = _tryGetDustAt(cell);
        if (dust == null) return 0f;
        return Resolve(cell, dust.Role, "VelocityDrain").carveResistance01;
    }

    public void ClearLoggedContexts() => _loggedInvalidResistanceContexts.Clear();
}
