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

/// <summary>
/// This class already exists in your project; keeping the same name to avoid asset breakage.
/// Only change is semantic: when type == NoteSpawner, trackModifierType is ignored.
/// </summary>
[Serializable]
public class WeightedMineNode
{
    public string label;

    public MinedObjectType minedObjectType = MinedObjectType.NoteSpawner;
    
    public MusicalRole role;                      // optional: used by your NodeFitsTrack policy
    public List<MusicalPhase> allowedPhases = new List<MusicalPhase>();

    [Min(1)] public int weight = 1;
    public int quota = -1;                        // optional future use
    public int rarity = 0;                        // optional future use
}
