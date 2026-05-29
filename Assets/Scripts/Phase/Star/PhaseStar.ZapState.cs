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
        return _zapProgressState == ZapProgressState.ReadyLatched && HasDominantRoleEjectable();
    }

    private NoteSet ResolvePlannedNoteSet(InstrumentTrack track)
    {
        if (track == null) return null;
        ResolveGameFlowManager();
        int entropy = CurrentEntropyForSelection();
        return _gfm != null ? _gfm.GenerateNotes(track, entropy) : null;
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
            {
                int persistentTemplateCount = planned.persistentTemplate != null ? planned.persistentTemplate.Count : 0;
                int distinctStepCount = planned.GetStepList()?.Distinct().Count() ?? 0;
                int noteListCount = planned.GetNoteList()?.Count ?? 0;
                noteCount = Mathf.Max(persistentTemplateCount, Mathf.Max(distinctStepCount, noteListCount));
            }
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
        Debug.Log($"[PhaseStar:Zap] refreshed role={_requiredZapRole} requiredZaps={requiredZapCount} currentZaps={zappedCount} changed={descriptorChanged} reason={reason}");
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
            _state != PhaseStarState.Dormant ||
            _coordinatorLockOwnedByOtherStar;
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
        Debug.Log($"[PhaseStar:ZapState] {prev}->{next} role={role} zappedCount={zappedCount} requiredZapCount={requiredZapCount} acquisitionEnabled={acquisitionEnabled} reason={reason} interaction=({_interactionState.ToDebugString()})");
    }

    public void OnCoordinatorLockOwnedByAnotherStar()
    {
        if (allowConcurrentDormantCharging)
            return;

        if (_state != PhaseStarState.Dormant)
            return;

        bool canSuspend =
            _zapProgressState == ZapProgressState.Seeking ||
            _zapProgressState == ZapProgressState.Zapping ||
            _zapProgressState == ZapProgressState.WaitingForRetract ||
            _zapProgressState == ZapProgressState.ReadyLatched;

        if (!canSuspend)
            return;

        _coordinatorLockOwnedByOtherStar = true;
        _preservedZapProgressStateBeforeCoordinatorLock = _zapProgressState;
        TransitionZapState(ZapProgressState.DormantNotSeeking, _requiredZapRole, "coordinator-lock-owned-by-other");
    }

    public void OnCoordinatorLockReleasedAfterOwnerCooldown()
    {
        if (allowConcurrentDormantCharging)
            return;

        _coordinatorLockOwnedByOtherStar = false;

        if (_state != PhaseStarState.Dormant)
            return;

        if (_zapProgressState == ZapProgressState.ReadyLatched)
        {
            TransitionDormantToActive();
            return;
        }

        if (_zapProgressState != ZapProgressState.DormantNotSeeking)
            return;

        var restore = _preservedZapProgressStateBeforeCoordinatorLock;
        bool wasPreservedReady = restore == ZapProgressState.ReadyLatched || restore == ZapProgressState.WaitingForRetract;
        var next = wasPreservedReady ? ZapProgressState.WaitingForRetract : ZapProgressState.Seeking;
        TransitionZapState(next, _requiredZapRole, "coordinator-lock-released-owner-cooldown");

        if (next == ZapProgressState.WaitingForRetract)
            TransitionDormantToActive();
    }
}
