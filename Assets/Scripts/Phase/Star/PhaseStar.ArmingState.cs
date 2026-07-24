using UnityEngine;

public partial class PhaseStar
{
    private IPhaseStarStateController _stateController;
    private IPhaseStarBurstCoordinator _burstCoordinator;

    // _awaitingCollectableClear: true while we are blocked waiting for in-flight collectables
    // to land before re-arming the star. Two independent timers enforce a timeout so a
    // stalled or destroyed collectable can't lock the star forever:
    //   • SinceLoop — counts loop boundaries elapsed (reliable in sync with music tempo).
    //   • SinceDsp  — counts real wall-clock DSP seconds (catches edge cases where loop
    //                 boundaries stop firing, e.g. if DrumTrack is paused mid-flight).
    // Both timers start on the first loop boundary that arrives while waiting. -1 means unset.
    private int _awaitingCollectableClearSinceLoop = -1;
    private double _awaitingCollectableClearSinceDsp = -1.0;

    private PhaseStarInteractionSnapshot BuildInteractionSnapshot(bool anyCollectablesInFlight = false, bool anyExpansionPending = false)
    {
        return new PhaseStarInteractionSnapshot(
            _state,
            _waitForDustCo != null,
            _activeNode != null || _activeSuperNode != null,
            anyCollectablesInFlight,
            anyExpansionPending,
            ZapProgress01 >= 1f,
            _interactionState.Interaction);
    }

    private bool TryEnterBurstHidden()
    {
        _burstCoordinator ??= new PhaseStarBurstCoordinator();
        bool burstOffScreen = _burstOffScreen;
        bool changed = _burstCoordinator.TryEnterBurstHidden(ref burstOffScreen);
        _burstOffScreen = burstOffScreen;
        return changed;
    }

    private bool TryExitBurstHidden()
    {
        _burstCoordinator ??= new PhaseStarBurstCoordinator();
        bool burstOffScreen = _burstOffScreen;
        bool changed = _burstCoordinator.TryExitBurstHidden(ref burstOffScreen);
        _burstOffScreen = burstOffScreen;
        return changed;
    }

    private void ArmNext()
    {
        _stateController ??= new PhaseStarStateController();
        if (!_stateController.CanArm(BuildInteractionSnapshot())) return;

        if (OwnTrackCollectablesInFlight())
        {
            Disarm(PhaseStarDisarmReason.CollectablesInFlight);
            return;
        }

        // Mirror ShouldDisarmForGlobalGates(): a fully-charged star is exempt from the
        // expansion-pending gate. Blocking it here would leave it permanently un-armable
        // while expansion is active (the loop boundary's "EP + ready = hold armed" guard
        // also returns early, so the re-arm path in section 3 is never reached).
        if (AnyExpansionPendingGlobal() && ZapProgress01 < 1f)
        {
            DBG("ArmNext: blocked by ExpansionPending -> Disarm:ExpansionPendingGlobal");
            Disarm(PhaseStarDisarmReason.ExpansionPending);
            return;
        }

        _disarmReason = PhaseStarDisarmReason.None;
        _isArmed = true;

        EnableColliders();
        dust?.SetTentaclesActive(false);

        motion?.SetOverrideTarget(null);
        motion?.SetSpeedMultiplier(1f);

        if (visuals != null && ZapProgress01 >= 1f)
            visuals.transform.localScale = Vector3.one;
        SetVisual(VisualMode.Bright, ResolvePreviewColorByReadiness());
    }

    public void Pause()
    {
        Disarm(PhaseStarDisarmReason.SiblingActive);
        motion?.SetFrozen(true);
    }

    public void Resume()
    {
        if (_isDisposing) return;
        _disarmReason = PhaseStarDisarmReason.None;
        if (_burstOffScreen)
        {
            if (TryExitBurstHidden())
                motion?.Enable(true);
            EnterDormantWaitState();
            return;
        }
        motion?.SetFrozen(false);
        motion?.Enable(true);
        if (IsEjectionReady())
        {
            ArmNext();
            if (!_isArmed)
                EnterDormantWaitState();
            return;
        }

        // When resumed after a sibling DiscoveryTrackNode flow, a non-ready dormant star must
        // re-enter dormant wait so tentacle acquisition restarts. Showing dim alone
        // leaves tentacles disabled and the star appears stuck despite valid dust.
        if (_state == PhaseStarState.Dormant)
        {
            EnterDormantWaitState();
            return;
        }

        if (!_isArmed)
        {
            ArmNext();
            if (!_isArmed)
                EnterDormantWaitState();
            return;
        }

        else
            visuals?.ShowDim(ResolvePreviewColorByReadiness());
    }

    // Hide the star in place for the duration of a burst — it stays at its world position.
    // Guarded — safe to call repeatedly; only executes on the first call per burst.
    private void HideInPlaceForBurst()
    {
        if (!TryEnterBurstHidden()) return;

        StopManagedCoroutine(ref _waitForDustCo);

        // Stay at current world position — do NOT teleport off-screen.
        motion?.Enable(false);
        motion?.SetFrozen(true);
        visuals?.HideAll();
        dust?.SetTentaclesActive(false);
        DisableColliders();
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] Burst in flight — hidden in place at {transform.position}");
    }

    private void Disarm(PhaseStarDisarmReason reason, Color? tintOverride = null)
    {
        _isArmed = false;
        _disarmReason = reason;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] Disarm reason={reason} star={name}");

        DisableColliders();

        // CollectablesInFlight: move off-screen for the burst duration.
        if (reason == PhaseStarDisarmReason.CollectablesInFlight)
            HideInPlaceForBurst();

        // Scout should not be visible while disarmed.
        dust?.SetTentaclesActive(false);

        // Suppress the dim visual when the star is parked off-screen.
        // NodeResolving: star is hidden (DiscoveryTrackNode is alive); use Hidden.
        // Also hide completely if a DiscoveryTrackNode/SuperNode is still live, regardless of reason
        // (e.g. ExpansionPending fires on a loop boundary while a node is active).
        // All other reasons: show dim so the star is faintly visible while waiting.
        if (!_burstOffScreen)
        {
            bool nodeIsAlive = _activeNode != null || _activeSuperNode != null;
            bool hideCompletely = reason == PhaseStarDisarmReason.NodeResolving
                               || nodeIsAlive;
            SetVisual(hideCompletely ? VisualMode.Hidden : VisualMode.Dim,
                      tintOverride ?? ResolvePreviewColorByReadiness());
        }

        OnDisarmed?.Invoke(this);
    }

    private bool CanAdvancePhaseNow()
    {
        // Enforce "no skip capture"
        if (_activeNode != null) return false;
        if (_activeSuperNode != null) return false;
        if (_ejectionInFlight) return false;
        return true;
    }

    private void OnLoopBoundary_RearmIfNeeded()
    {
        bool cif = OwnTrackCollectablesInFlight();
        bool ep = AnyExpansionPendingGlobal();

        if (GameFlowManager.VerboseLogging) Debug.Log(
            $"[PS:LB] star={name} state={_state} armed={_isArmed} disarm={_disarmReason} " +
            $"awaitClr={_awaitingCollectableClear} " +
            $"activeNode={(_activeNode ? _activeNode.name : null)} ejectInFlight={_ejectionInFlight} CIF={cif} EP={ep}");

        if (_isDisposing || this == null) return;
        if (_disarmReason == PhaseStarDisarmReason.SiblingActive) return;

        if (HandleAwaitingCollectableClear()) return;

        bool anyCollectables = OwnTrackCollectablesInFlight();
        bool anyExpansion = AnyExpansionPendingGlobal();
        if (HandleGlobalGateCheck(anyCollectables, anyExpansion)) return;

        LogState("LoopBoundary entry");
        ExecuteRearmPath();
    }

    private bool HandleAwaitingCollectableClear()
    {
        if (!_awaitingCollectableClear) return false;

        if (AnyCollectablesInFlightGlobal() && (_activeNode != null || _activeSuperNode != null || _ejectionInFlight))
        {
            if (GameFlowManager.VerboseLogging) Debug.Log("[PS:LB/AWAIT] -> stay disarmed (awaitClr + CIF + active node)");
            Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
            return true;
        }

        ResolveGameFlowManager();
        var drums = _drum != null ? _drum : _gfm?.activeDrumTrack;

        bool timedOut = false;

        if (CollectableClearTimeoutLoops > 0 && drums != null)
        {
            int nowLoop = drums.completedLoops;
            if (_awaitingCollectableClearSinceLoop < 0)
                _awaitingCollectableClearSinceLoop = nowLoop;

            int waitedLoops = nowLoop - _awaitingCollectableClearSinceLoop;
            if (waitedLoops >= CollectableClearTimeoutLoops)
                timedOut = true;

            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[PS:LB/AWAIT.LOOPS] nowLoop={nowLoop} sinceLoop={_awaitingCollectableClearSinceLoop} waitedLoops={waitedLoops} loopsTimeout={CollectableClearTimeoutLoops} -> timedOut={timedOut}");
        }

        if (!timedOut && CollectableClearTimeoutSeconds > 0f)
        {
            double nowDsp = AudioSettings.dspTime;
            if (_awaitingCollectableClearSinceDsp < 0.0)
                _awaitingCollectableClearSinceDsp = nowDsp;

            if ((nowDsp - _awaitingCollectableClearSinceDsp) >= CollectableClearTimeoutSeconds)
                timedOut = true;
        }

        if (!timedOut)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PS:LB/AWAIT] -> continue waiting (not timed out)");
            Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
            return true;
        }

        Debug.LogWarning($"[PhaseStar][Timeout] AwaitingCollectableClear timed out. Forcing recovery. star={name}");

        _awaitingCollectableClear = false;
        _awaitingCollectableClearSinceLoop = -1;
        _awaitingCollectableClearSinceDsp = -1.0;

        if (!CanAdvancePhaseNow())
        {
            Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
            return true;
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[PS:LB] Recovery -> Dormant wait");
        EnterDormantWaitState();
        return true;
    }

    private bool HandleGlobalGateCheck(bool anyCollectables, bool anyExpansion)
    {
        bool shouldDisarmForGate = _stateController?.ShouldDisarmForGlobalGates(BuildInteractionSnapshot(anyCollectables, anyExpansion)) ?? false;

        if (shouldDisarmForGate)
        {
            if (GameFlowManager.VerboseLogging)
                Debug.Log(anyCollectables ? "[PS:LB] OwnTrackCollectablesInFlight True" : "[PS:LB] AnyExpansionPending True");

            Disarm(anyCollectables ? PhaseStarDisarmReason.CollectablesInFlight : PhaseStarDisarmReason.ExpansionPending, _lockedTint);
            return true;
        }

        // Only hold when already armed. Without the _isArmed check, a fully-charged
        // but un-armed star (e.g. just resumed after a sibling's cycle) would skip the
        // re-arm path and never reach ArmNext().
        if (anyExpansion && ZapProgress01 >= 1f && _isArmed)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PS:LB] EP true but star is ready — holding armed");
            return true;
        }

        return false;
    }

    private void ExecuteRearmPath()
    {
        if (!_isArmed)
        {
            // Stale burst-hide: all global gates cleared, so _burstOffScreen from a prior
            // CIF-triggered hide is safe to reset. Restore motion before arming.
            // This must run before the WaitingForPoke branch so CanArm() sees BurstOffScreen=false.
            if (_burstOffScreen)
            {
                if (TryExitBurstHidden())
                {
                    motion?.Enable(true);
                    motion?.SetFrozen(false);
                }
            }

            DBG("[PS:LB] -> re-arm");
            if (_state == PhaseStarState.WaitingForPoke)
            {
                ArmNext();
            }
            else
            {
                // Only re-enter dormant wait if not already committed to a retract/latch sequence.
                // EnterDormantWaitState() calls SetTentaclesActive(true), which re-enables
                // acquisition and starts a new grow/drain/retract cycle — preventing
                // ReadyLatched from ever being reached until all matching dust is exhausted.
                // The recovery guard in UpdateStateRecovery() and OnAllTentaclesRetracted()
                // will advance the star once retractions finish.
                bool retractOrLatchInProgress =
                    _zapProgressState == ZapProgressState.WaitingForRetract ||
                    _zapProgressState == ZapProgressState.ReadyLatched ||
                    _pendingDormantActivation;

                if (retractOrLatchInProgress)
                {
                    DBG("[PS:LB] -> retract/latch committed, skip dormant re-enter");
                    return;
                }

                EnterDormantWaitState();
            }
        }
        else
        {
            // If the zap requirement changed (e.g. motif/phase swap) while this star stayed
            // armed, we can end up armed-but-not-latched with tentacles off, which deadlocks
            // poke flow. Drop back into dormant so zap acquisition can resume.
            if (_zapProgressState != ZapProgressState.ReadyLatched)
            {
                DBG("[PS:LB] armed but not ready-latched -> returning to dormant");
                Disarm(PhaseStarDisarmReason.None, _lockedTint);
                EnterDormantWaitState();
                return;
            }

            DBG("[PS:LB] -> No need to arm");
        }
    }
}
