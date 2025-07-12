using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class ConstellationMemoryStore
{
    private static List<PhaseSnapshot> cachedSnapshots = new();

    public static void StoreSnapshot(List<PhaseSnapshot> snapshots)
    {
        cachedSnapshots = snapshots.Select(s => new PhaseSnapshot
        {
            pattern = s.pattern,
            color = s.color,
            timestamp = s.timestamp,
            collectedNotes = s.collectedNotes
                .Select(n => new PhaseSnapshot.NoteEntry(n.step, n.note, n.velocity, n.trackColor))
                .ToList()
        }).ToList(); // Deep copy for safety
    }

    public static List<PhaseSnapshot> RetrieveSnapshot()
    {
        return cachedSnapshots;
    }

    public static void Clear()
    {
        cachedSnapshots.Clear();
    }
}