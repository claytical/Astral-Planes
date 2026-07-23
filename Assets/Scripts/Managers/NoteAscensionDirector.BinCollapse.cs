using UnityEngine;
using System.Collections.Generic;

public sealed partial class NoteAscensionDirector
{
    private void TryCollapseIfHighestBinEmpty(InstrumentTrackController ctrl)
    {
        if (ctrl?.tracks == null) return;

        // Find the global highest loop multiplier across all tracks.
        int globalMaxMult = 1;
        foreach (var t in ctrl.tracks)
            if (t != null) globalMaxMult = Mathf.Max(globalMaxMult, t.loopMultiplier);

        if (globalMaxMult <= 1) return; // already at minimum, nothing to trim

        // Determine the step range of the highest bin.
        // All tracks share the same drum track, so BinSize() is uniform.
        int binSize = 0;
        foreach (var t in ctrl.tracks)
        {
            if (t != null) { binSize = t.BinSize(); break; }
        }
        if (binSize <= 0) return;

        int highBinStart = (globalMaxMult - 1) * binSize;
        int highBinEnd   = globalMaxMult * binSize;

        // If any track still has a note in the highest bin, do not collapse.
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            foreach (var n in t.GetPersistentLoopNotes())
            {
                if (n.stepIndex >= highBinStart && n.stepIndex < highBinEnd)
                    return;
            }
        }

        // If any track has in-transit notes (collectables or in Vehicle queue) in the
        // highest bin, defer collapse until those notes are resolved. Without this guard,
        // ForceSyncMarkersToPersistentLoop would destroy placeholder markers that the
        // Vehicle still needs for manual note release.
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            if (t.HasOutstandingNotesInRange(highBinStart, highBinEnd))
            {
                _deferredCollapseControllers ??= new HashSet<InstrumentTrackController>();
                _deferredCollapseControllers.Add(ctrl);
                if (GameFlowManager.VerboseLogging) Debug.Log($"[ASCENSION] Collapse deferred — track '{t.name}' has in-transit notes in bin [{highBinStart},{highBinEnd}). Will retry at next loop boundary.");
                return;
            }
        }

        // Guard: if the highest bin is allocated but not yet filled, an expansion burst
        // may have been enqueued via EnqueueNextFrame and hasn't spawned yet.
        // HasOutstandingNotesInRange won't catch it because burst steps aren't populated
        // until the next frame. Defer one boundary so the burst has time to register.
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            if (t.IsBinAllocated(globalMaxMult - 1) && !t.IsBinFilled(globalMaxMult - 1))
            {
                _deferredCollapseControllers ??= new HashSet<InstrumentTrackController>();
                _deferredCollapseControllers.Add(ctrl);
                if (GameFlowManager.VerboseLogging) Debug.Log($"[ASCENSION] Collapse deferred — highest bin {globalMaxMult - 1} allocated-but-unfilled on '{t.name}'; expansion burst may be in flight.");
                return;
            }
        }

        // Highest bin is empty on all tracks — collapse every track that owns it.
        // Skip any track that is mid-expansion; the expand and a concurrent collapse would
        // net zero at the next boundary and send the staged burst to the wrong bin.
        if (GameFlowManager.VerboseLogging) Debug.Log($"[ASCENSION] Highest bin {globalMaxMult - 1} empty on all tracks — collapsing loop by 1.");
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            if (t.IsExpansionPending)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[ASCENSION] Skipping collapse on '{t.name}' — expansion pending; will re-check at next boundary.");
                _deferredCollapseControllers ??= new HashSet<InstrumentTrackController>();
                _deferredCollapseControllers.Add(ctrl);
                continue;
            }
            if (t.loopMultiplier >= globalMaxMult)
                t.RequestLoopCollapseByOne();
        }
    }
}
