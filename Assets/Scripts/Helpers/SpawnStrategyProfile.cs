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
    public MusicalRole PeekRoleAtCycleIndex(int cycleIndex)
    {
        if (roleSequence == null || roleSequence.Count == 0) return MusicalRole.Bass; // or a safe default
        int idx = ((cycleIndex % roleSequence.Count) + roleSequence.Count) % roleSequence.Count;
        return roleSequence[idx];
    }

    public MusicalRole PeekRoleAtOffset(int offset, int nodesPerStar)
    {
        if (roleSequence == null || roleSequence.Count == 0) return MusicalRole.Bass;

        // “cropped/repeated to nodesPerStar”
        int effectiveLen = Mathf.Max(1, nodesPerStar);
        int idxWithinStar = Mathf.Clamp(offset, 0, effectiveLen - 1);

        int seqIdx = idxWithinStar % roleSequence.Count;
        return roleSequence[seqIdx];
    }

    public MusicalRole ConsumeNextRole(int nodesPerStar)
    {
        var role = PeekRoleAtOffset(_cursor, nodesPerStar);
        _cursor++;
        return role;
    }
}
