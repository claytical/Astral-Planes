using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Spawn Strategy Profile")]
public class SpawnStrategyProfile : ScriptableObject
{
    // Authored sequence; duplicates are the “weights”
    public List<MusicalRole> roleSequence = new();

    // runtime cursor (persists across ejections on this star)
    [NonSerialized] private int _cursor;

    public void ResetForNewStar() => _cursor = 0;

    // All four playable roles used as the fallback round-robin when no authored sequence is set.
    private static readonly MusicalRole[] _allRoles =
    {
        MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove
    };

    public MusicalRole PeekRoleAtOffset(int offset, int nodesPerStar)
    {
        int effectiveLen = Mathf.Max(1, nodesPerStar);
        int idxWithinStar = Mathf.Clamp(offset, 0, effectiveLen - 1);

        // Use the authored sequence when it contains more than one distinct role.
        // A single-role sequence (or empty) would cause Bass to dominate; fall back
        // to a round-robin of all four roles so the PhaseStar always shows variety.
        if (roleSequence != null && roleSequence.Count >= 2)
        {
            // Count distinct roles — if only one, treat as unconfigured.
            bool hasVariety = false;
            for (int i = 1; i < roleSequence.Count; i++)
            {
                if (roleSequence[i] != roleSequence[0]) { hasVariety = true; break; }
            }
            if (hasVariety)
            {
                int seqIdx = idxWithinStar % roleSequence.Count;
                return roleSequence[seqIdx];
            }
        }

        // Fallback: even round-robin across all four roles.
        return _allRoles[idxWithinStar % _allRoles.Length];
    }

}
