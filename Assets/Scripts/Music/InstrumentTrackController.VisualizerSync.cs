using System.Linq;
using UnityEngine;

// Keeps NoteVisualizer markers synced to each track's persistent loop, change-detected via a
// cheap order-independent hash so unchanged tracks skip marker resync work.
public partial class InstrumentTrackController
{
    public void UpdateVisualizer()
    {
        if (noteVisualizer == null || tracks == null) return;

        foreach (var track in tracks)
        {
            if (track == null) continue;

            int h = ComputeLoopHash(track);
            if (_loopHash.TryGetValue(track, out var prev) && prev == h)
                continue; // no loop change → no work this frame

            _loopHash[track] = h;

            // Subtractive-safe: removes stale markers (steps no longer in persistent loop),
            // then ensures all remaining loop steps are represented.
            noteVisualizer.ForceSyncMarkersToPersistentLoop(track);
        }
    }

    private static int ComputeLoopHash(InstrumentTrack t)
    {
        if (t == null) return 0;

        // Order-independent hash of loop steps (cheap + stable).
        // This only considers (stepIndex), which is enough to detect shrink/expand membership changes.
        unchecked
        {
            int h = 17;

            var loop = t.GetPersistentLoopNotes();
            if (loop == null) return h;

            foreach (var (step, _, _, _, _) in loop.OrderBy(n => n.Item1))
                h = h * 31 + step;

            return h;
        }
    }

    public int GetMaxActiveLoopMultiplier()
    {
        if (tracks == null || tracks.Length == 0) return 1;

        int maxMul = 1;
        foreach (var t in tracks)
        {
            if (t == null) continue;

            // “Committed” means “this track’s authoritative loop span,” regardless of whether
            // a particular bin is currently silent.
            maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
        }
        return maxMul;
    }

    // Same "max loopMultiplier across tracks" concept as GetMaxActiveLoopMultiplier;
    // kept as a separate name since callers use both, but delegates to avoid duplication.
    public int GetMaxLoopMultiplier() => GetMaxActiveLoopMultiplier();
}
