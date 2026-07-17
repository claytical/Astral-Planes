using System.Linq;
using UnityEngine;

public partial class PhaseStar
{
    private bool IsEjectionReady()
    {
        if (!_plannedEjectionDescriptor.IsValid)
        {
            if (!_missingDescriptorWarned)
            {
                Debug.LogWarning($"[PhaseStar:Zap] planned ejection descriptor missing; readiness blocked. role={_requiredZapRole} track={_plannedEjectionDescriptor.track?.name ?? "null"}");
                _missingDescriptorWarned = true;
            }
            return false;
        }

        _missingDescriptorWarned = false;
        return _zapProgressState == ZapProgressState.ReadyLatched;
    }

    private NoteSet ResolvePlannedNoteSet(InstrumentTrack track)
    {
        if (track == null) return null;
        ResolveGameFlowManager();
        return _gfm != null ? _gfm.GenerateNotes(track, 0) : null;
    }

    private static bool PlannedDescriptorEquals(in PlannedEjectionDescriptor a, in PlannedEjectionDescriptor b)
    {
        return a.role == b.role &&
               ReferenceEquals(a.track, b.track) &&
               ReferenceEquals(a.noteSet, b.noteSet) &&
               a.requiredZapCount == b.requiredZapCount;
    }

    private bool TryRefreshRequiredZapCountForPlannedRole(
        MusicalRole role,
        InstrumentTrack track,
        bool resetCurrentZapCount,
        string reason)
    {
        _requiredZapRole = role;
        _requiredZapNoteSetAvailable = false;

        PlannedEjectionDescriptor previousDescriptor = _plannedEjectionDescriptor;
        PlannedEjectionDescriptor nextDescriptor = default;
        nextDescriptor.role = role;
        nextDescriptor.track = track;

        if (track == null)
        {
            requiredZapCount = int.MaxValue;
            _plannedEjectionDescriptor = nextDescriptor;
            Debug.LogWarning($"[PhaseStar:Zap] missing track for required zap refresh. role={role} reason={reason}");
            return false;
        }

        NoteSet planned = ResolvePlannedNoteSet(track);

        int noteCount;
        if (planned == null)
        {
            // NoteSet not yet generated (prebuilt missing, motif not applied yet).
            // Fall back to authoritative riff count so the star can still reach
            // ReadyLatched — DirectSpawnMineNode will generate the NoteSet fresh.
            if (!TryResolveAuthoritativeZapCount(role, track, out noteCount) || noteCount <= 0)
            {
                requiredZapCount = int.MaxValue;
                _plannedEjectionDescriptor = nextDescriptor;
                Debug.LogWarning($"[PhaseStar:Zap] planned NoteSet unavailable and no authoritative riff count; blocking readiness. role={role} track={track.name} reason={reason}");
                return false;
            }
            Debug.LogWarning($"[PhaseStar:Zap] planned NoteSet unavailable; using authoritative riff count={noteCount}. role={role} track={track.name} reason={reason}");
        }
        else
        {
            if (!TryResolveAuthoritativeZapCount(role, track, out noteCount))
                noteCount = GetNoteSetNoteCount(planned);
        }

        if (_currentBurstRequiredZaps > 0)
            noteCount = Mathf.Max(noteCount, _currentBurstRequiredZaps);

        nextDescriptor.noteSet = planned;
        nextDescriptor.requiredZapCount = Mathf.Max(1, noteCount);

        _requiredZapNoteSetAvailable = nextDescriptor.IsValid;
        requiredZapCount = nextDescriptor.requiredZapCount;

        bool descriptorChanged = !PlannedDescriptorEquals(previousDescriptor, nextDescriptor);
        _plannedEjectionDescriptor = nextDescriptor;

        if (resetCurrentZapCount)
            zappedCount = 0;

        if (resetCurrentZapCount)
        {
            TransitionZapState(ZapProgressState.Seeking, role, $"refresh:{reason}");
        }
        else if (_zapProgressState == ZapProgressState.Seeking || _zapProgressState == ZapProgressState.Zapping)
        {
            TransitionZapState(ZapProgressState.Zapping, role, $"refresh:{reason}");
        }
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar:Zap] refreshed role={_requiredZapRole} requiredZaps={requiredZapCount} currentZaps={zappedCount} changed={descriptorChanged} reason={reason}");
        return true;
    }

    private void PrimeZapRequirementForRole(MusicalRole role, InstrumentTrack track)
    {
        TryRefreshRequiredZapCountForPlannedRole(role, track, resetCurrentZapCount: true, reason: "prime");
    }

    private bool MayAcquireDustTargets()
    {
        bool zapStateAllowsAcquire =
            _zapProgressState == ZapProgressState.Seeking ||
            _zapProgressState == ZapProgressState.Zapping;
        bool globallyGatedOrDisarmed =
            _disarmReason != PhaseStarDisarmReason.None ||
            _state != PhaseStarState.Dormant;
        return zapStateAllowsAcquire && !globallyGatedOrDisarmed;
    }

    private void ApplyDustAcquisitionPolicy(string reason)
    {
        bool enabled = MayAcquireDustTargets();
        dust?.SetAcquisitionEnabled(enabled, $"{reason}|zap={_zapProgressState}|state={_state}|disarm={_disarmReason}");
    }

    private void TransitionZapState(ZapProgressState next, MusicalRole role, string reason)
    {
        if (_zapProgressState == next) return;
        var prev = _zapProgressState;
        _zapProgressState = next;
        ApplyDustAcquisitionPolicy($"zap-transition:{reason}");
        bool acquisitionEnabled = MayAcquireDustTargets();
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar:ZapState] {prev}->{next} role={role} zappedCount={zappedCount} requiredZapCount={requiredZapCount} acquisitionEnabled={acquisitionEnabled} reason={reason} interaction=({_interactionState.ToDebugString()})");
    }

}
