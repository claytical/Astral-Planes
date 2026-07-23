using System.Linq;
using UnityEngine;

public sealed partial class StarPool
{
    // ── Collectable burst tracking ────────────────────────────────────────────

    private void SubscribeToTracks()
    {
        if (_tracks == null) return;
        foreach (var track in _tracks)
        {
            if (track == null) continue;
            track.OnCollectableBurstCleared -= HandleCollectableBurstCleared;
            track.OnCollectableBurstCleared += HandleCollectableBurstCleared;
        }
    }

    private void UnsubscribeFromTracks()
    {
        if (_tracks == null) return;
        foreach (var track in _tracks)
        {
            if (track == null) continue;
            track.OnCollectableBurstCleared -= HandleCollectableBurstCleared;
        }
    }

    private bool AnyCollectablesInFlight()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        return _gfm != null && _gfm.AnyCollectablesInFlightGlobal();
    }

    private bool AnyVehicleCapturedCollectablesPendingRelease()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var vehicles = _gfm?.GetVehicles();
        if (vehicles == null) return false;

        for (int i = 0; i < vehicles.Count; i++)
        {
            var vehicle = vehicles[i];
            if (vehicle != null && vehicle.HasCapturedCollectablesPendingRelease())
                return true;
        }

        return false;
    }

    private bool HasUnresolvedMineNodeSequence()
        => AnyCollectablesInFlight() || AnyVehicleCapturedCollectablesPendingRelease();

    // ── Bridge gate ───────────────────────────────────────────────────────────

    private void CheckBridgeGate()
    {
        if (_remainingEjectionsTotal > 0)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: {_remainingEjectionsTotal} ejections remaining — blocked");
            return;
        }
        if (_activeStars.Values.Any(s => s != null)) { if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: activeStars still live — blocked"); return; }
        if (_pausedStars.Any(s => s != null)) { if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: pausedStars still live — blocked"); return; }
        if (HasUnresolvedMineNodeSequence()) { if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: CIF — blocked"); return; }

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gfm = _gfm;
        if (gfm == null) return;

        // If no nodes were captured this motif and all track loops are still empty,
        // the player had no successful interaction — restart the same motif instead of bridging.
        if (_nodesCapturedThisMotif == 0 && AllTracksEmpty())
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: zero captures, empty loops — restarting motif.");
            BuildPhasePlan();
            return;
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: PASS remainingTotal=0 captured={_nodesCapturedThisMotif} — triggering bridge.");
        gfm.BeginMotifBridge("StarPool");
    }

    private bool AllTracksEmpty()
    {
        if (_tracks == null) return true;
        foreach (var track in _tracks)
        {
            if (track == null) continue;
            var notes = track.GetPersistentLoopNotes();
            if (notes != null && notes.Count > 0) return false;
        }
        return true;
    }
}
