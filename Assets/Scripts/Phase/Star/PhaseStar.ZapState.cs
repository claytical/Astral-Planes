using System.Linq;
using UnityEngine;

public partial class PhaseStar
{
    private struct PlannedEjectionDescriptor
    {
        public MusicalRole role;
        public InstrumentTrack track;
        public NoteSet noteSet;
        public int requiredZapCount;

        public bool IsValid => role != MusicalRole.None && track != null && requiredZapCount > 0;
    }

    // Zap progress state machine.
    // Seeking          → star has no zaps yet and is accumulating.
    // Zapping          → at least one zap delivered, still accumulating toward threshold.
    // WaitingForRetract → threshold met; tentacles are retracting before readiness is latched.
    // ReadyLatched      → all zaps confirmed + tentacles retracted; poke can now eject a node.
    // Ejecting          → node spawn in flight.
    private enum ZapProgressState { Seeking, Zapping, WaitingForRetract, ReadyLatched, Ejecting }
    private ZapProgressState _zapProgressState = ZapProgressState.Seeking;

    private PlannedEjectionDescriptor _plannedEjectionDescriptor;
    private bool _missingDescriptorWarned;

    // Zap count authority chain (highest to lowest priority):
    //   1. _currentBurstRequiredZaps — set by DirectSpawnMineNode() from the actual node payload;
    //      overrides everything else because the node is already spawned.
    //   2. TryResolveAuthoritativeZapCount() — motif/phase authored count from the NoteSet.
    //   3. NoteSet cardinality fallback (Max of persistentTemplate, distinct steps, note list).
    // requiredZapCount stores the resolved value; _currentBurstRequiredZaps is the burst-phase
    // floor so tentacle pool sizing stays stable even if requiredZapCount refreshes mid-cycle.
    private int zappedCount;
    private int requiredZapCount = 1;
    private int _currentBurstRequiredZaps = 0;
    public int RequiredZapCount => Mathf.Max(1, requiredZapCount);
    public int RemainingZapCount => Mathf.Max(0, RequiredZapCount - Mathf.Max(0, zappedCount));
    public float ZapProgress01 => Mathf.Clamp01((float)Mathf.Max(0, zappedCount) / Mathf.Max(1, RequiredZapCount));
    public int GetDesiredTentacleCount()
    {
        // Take the max of both counts: requiredZapCount may still be catching up from a
        // refresh probe on the first frame after a burst spawn.
        return Mathf.Max(1, Mathf.Max(RequiredZapCount, _currentBurstRequiredZaps));
    }
    private MusicalRole _requiredZapRole = MusicalRole.None;
    private bool _requiredZapNoteSetAvailable;
    private MusicalRole _lastResolvedZapRole = MusicalRole.None;

    private void OnDustDelivery(MusicalRole role, float deliveredUnits)
    {
        _hasReceivedEnergy = true;
        if (deliveredUnits <= 0f || role == MusicalRole.None) return;
        if (_zapProgressState == ZapProgressState.Seeking)
            TransitionZapState(ZapProgressState.Zapping, role, "first-delivery");
    }

    public void OnTentacleZapResolved(MusicalRole role, Vector2Int targetCell)
    {
        if (role == MusicalRole.None) return;

        // Once readiness has been reached for this cycle, ignore late/extra resolves so
        // additional tentacles cannot keep extending the cycle.
        if (_zapProgressState == ZapProgressState.WaitingForRetract ||
            _zapProgressState == ZapProgressState.ReadyLatched ||
            _zapProgressState == ZapProgressState.Ejecting)
            return;

        // Canonical zap progress path: increment exactly once per confirmed dust clear.
        zappedCount++;
        _lastResolvedZapRole = role;

        if (!_requiredZapNoteSetAvailable || !_plannedEjectionDescriptor.IsValid)
        {
            // Self-heal: derive descriptor from the role that actually resolved a zap.
            // Without this, stars can become ReadyLatched with an invalid descriptor
            // and remain unejectable while acquisition stays disabled.
            var resolvedTrack = FindTrackByRole(role);
            if (resolvedTrack != null)
            {
                TryRefreshRequiredZapCountForPlannedRole(
                    role,
                    resolvedTrack,
                    resetCurrentZapCount: false,
                    reason: "zap-resolved-descriptor-repair");
            }
            Debug.LogWarning($"[PhaseStar:ZapResolved] missing planned ejection descriptor; readiness blocked. role={role} track={_plannedEjectionDescriptor.track?.name ?? "null"}");
        }

        bool readyNow = zappedCount >= requiredZapCount;
        if (readyNow)
        {
            TransitionZapState(ZapProgressState.WaitingForRetract, role, "count-threshold-met");
            dust?.SetAcquisitionEnabled(false, "waiting-for-retract-threshold-met");
            dust?.BeginRetractionForActiveTentacles();

            if (_state == PhaseStarState.Dormant && !_pendingDormantActivation)
                TransitionDormantToActive();
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar:ZapResolved] role={role} targetCell={targetCell} requiredZaps={requiredZapCount} currentZaps={zappedCount} ready={readyNow}");
    }

    private void OnAllTentaclesRetracted()
    {
        if (_pendingDormantActivation)
            FinalizeDormantToActiveAfterRetract();
        else if (_state == PhaseStarState.Dormant &&
                 _zapProgressState == ZapProgressState.WaitingForRetract)
            FinalizeDormantToActiveAfterRetract(force: true);

        if (_zapProgressState != ZapProgressState.WaitingForRetract)
            return;

        // Safety: only latch readiness if zap requirements are truly satisfied.
        bool canLatchReady = zappedCount >= requiredZapCount;
        if (!canLatchReady)
        {
            MusicalRole fallbackRole = _requiredZapRole != MusicalRole.None ? _requiredZapRole : _previewRole;
            var fallback = zappedCount > 0 ? ZapProgressState.Zapping : ZapProgressState.Seeking;
            TransitionZapState(fallback, fallbackRole, "retract-without-required-zaps");
            return;
        }

        MusicalRole latchedRole = _requiredZapRole != MusicalRole.None ? _requiredZapRole : _previewRole;

        // Descriptor must be valid before latching readiness, otherwise the star can
        // stop acquiring dust and never become ejectable.
        if (!_plannedEjectionDescriptor.IsValid || !_requiredZapNoteSetAvailable)
        {
            MusicalRole repairRole = latchedRole != MusicalRole.None ? latchedRole : _lastResolvedZapRole;
            if (repairRole != MusicalRole.None)
            {
                var repairTrack = FindTrackByRole(repairRole);
                if (repairTrack != null)
                {
                    TryRefreshRequiredZapCountForPlannedRole(
                        repairRole,
                        repairTrack,
                        resetCurrentZapCount: false,
                        reason: "retract-descriptor-repair");
                }
            }
        }

        if (!_plannedEjectionDescriptor.IsValid || !_requiredZapNoteSetAvailable)
        {
            MusicalRole fallbackRole = _lastResolvedZapRole != MusicalRole.None ? _lastResolvedZapRole : latchedRole;
            var fallback = zappedCount > 0 ? ZapProgressState.Zapping : ZapProgressState.Seeking;
            TransitionZapState(fallback, fallbackRole, "retract-descriptor-invalid");
            dust?.SetAcquisitionEnabled(true, "retract-descriptor-invalid-resume-acquire");
            return;
        }

        TransitionZapState(ZapProgressState.ReadyLatched, latchedRole, "all-tentacles-retracted");
        dust?.SetAcquisitionEnabled(false, "ready-latched-keep-disabled");
    }

    private Color ResolveRoleColor(MusicalRole role, InstrumentTrack fallbackTrack = null)
    {
        var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
        if (roleProfile != null)
        {
            int voiceIdx = fallbackTrack?.voiceIndex ?? 0;
            var c = roleProfile.GetColorForVoice(voiceIdx);
            return new Color(c.r, c.g, c.b, 1f);
        }

        if (fallbackTrack != null)
        {
            var c = fallbackTrack.DisplayColor;
            return new Color(c.r, c.g, c.b, 1f);
        }

        return Color.white;
    }

    private void OnAttuned_SetRole(MusicalRole role)
    {
        if (_attunedRole != MusicalRole.None) return;
        _attunedRole = role;
        cravingNavigator?.SetAttunedRole(role);
        dust?.SetAttunedRole(role);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] Attuned to {role}");

        // Prime before first zap so requiredZapCount isn't still 1 when the first dust clears.
        if (_assignedMotif != null && zappedCount == 0)
        {
            var track = FindTrackByRole(role);
            if (track != null)
                TryRefreshRequiredZapCountForPlannedRole(role, track, resetCurrentZapCount: false, reason: "attune-prime");
        }
    }

    private Color ResolvePreviewColorByReadiness()
    {
        if (_previewRole == MusicalRole.None) return Color.gray;

        float ready01 = _zapProgressState == ZapProgressState.ReadyLatched ? 1f : ZapProgress01;
        Color gray = Color.Lerp(Color.gray, Color.lightGray, 0.65f);
        Color c = Color.Lerp(gray, _previewColor, ready01);
        c.a = 1f;
        return c;
    }

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
