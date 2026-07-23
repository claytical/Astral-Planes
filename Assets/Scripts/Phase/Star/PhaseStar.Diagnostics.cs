using UnityEngine;

public partial class PhaseStar
{
    bool AnyExpansionPendingGlobal()
    {
        ResolveGameFlowManager();
        var tc = _gfm != null ? _gfm.controller : null;
        return (tc != null && tc.AnyExpansionPending());
    }

    /// <summary>Diagnostic-only: lists each track with IsExpansionPending==true and its flag breakdown.</summary>
    string DebugExpansionPendingTracks()
    {
        var tc = _gfm != null ? _gfm.controller : null;
        if (tc == null || tc.tracks == null) return "no-controller";

        var sb = new System.Text.StringBuilder();
        foreach (var t in tc.tracks)
        {
            if (t == null || !t.IsExpansionPending) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t.name).Append('[').Append(t.DebugExpansionState()).Append(']');
        }
        return sb.Length > 0 ? sb.ToString() : "none";
    }

    private bool AnyCollectablesInFlightGlobal()
    {
        var gfm = ResolveGameFlowManager();
        return gfm != null && gfm.AnyCollectablesInFlightGlobal();
    }

    // Returns true only when THIS star's own InstrumentTrack has live collectables,
    // ignoring collectables from other tracks so concurrent same-bin stars aren't blocked.
    private bool OwnTrackCollectablesInFlight()
    {
        var track = _cachedTrack ?? FindTrackByRole(_attunedRole);
        if (track == null) return AnyCollectablesInFlightGlobal(); // unknown track — be conservative
        track.PruneSpawnedCollectables();
        return track.spawnedCollectables != null && track.spawnedCollectables.Count > 0;
    }

    private void DBG(string msg)
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PSDBG] {msg} :: star={name} state={_state} interaction=({_interactionState.ToDebugString()}) " +
                  $"preview={(_previewVisual != null ? 1 : 0)} " +
                  $"activeNode={(_activeNode != null ? _activeNode.name : "null")} lockedTint={_lockedTint}");
    }

    private void Trace(string msg)
    {
        if (_tracePhaseStar)
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] {msg}");
    }

    private void LogState(string where)
    {
        if (_isDisposing || this == null || !_tracePhaseStar) return;

        string targetRole = _previewRole != MusicalRole.None ? _previewRole.ToString() : "-";
        if (GameFlowManager.VerboseLogging) Debug.Log(
            $"[PhaseStar][{where}] state={_state} interaction=({_interactionState.ToDebugString()}) " +
            $"role={targetRole} attunedRole={_attunedRole} zapped={zappedCount}/{RequiredZapCount}");
    }
}
