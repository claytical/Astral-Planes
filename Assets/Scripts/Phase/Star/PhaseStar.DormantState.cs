using System.Collections;
using UnityEngine;

public partial class PhaseStar
{
    private bool _pendingDormantActivation;
    private Coroutine _waitForDustCo;

    public void EnterInMaze(Vector2 worldPos)
    {
        EnsureSubcomponents();
        StopManagedCoroutine(ref _waitForDustCo);

        _state = PhaseStarState.Dormant;

        transform.position = (Vector3)worldPos + Vector3.forward * transform.position.z;

        visuals?.HideAll();
        DisableColliders();
        dust?.SetTentaclesActive(false);
        cravingNavigator?.SetActive(false);
        _isArmed = false;
        _disarmReason = PhaseStarDisarmReason.None;
        _burstOffScreen = false;
        _awaitingCollectableClear = false;
        _displayedCharge01 = 0f;
        TransitionZapState(ZapProgressState.Seeking, _previewRole, "phase-reset-enter-maze");

        if (motion != null)
        {
            motion.Enable(true);
            motion.SetFrozen(true);
            motion.SetSpeedMultiplier(0f);
            motion.SetOverrideTarget(null);
        }

        EnterDormantWaitState();
        LogState("EnterInMaze+Dormant");
    }

    private void EnterDormantWaitState()
    {
        _state = PhaseStarState.Dormant;
        _isArmed = false;
        _disarmReason = PhaseStarDisarmReason.None;

        visuals?.ToggleShardRenderers(true);

        float seedScale = Mathf.Max(0f, dormantSeedScale);
        Vector3 seed = Vector3.one * seedScale;
        if (visuals != null) visuals.transform.localScale = seed;
        if (_previewVisual != null) _previewVisual.localScale = seed;
        if (_previewVisualB != null) _previewVisualB.localScale = seed;
        visuals?.ShowDim(ResolvePreviewColorByReadiness());
        _dormantSeedVisualPrimed = true;

        DisableColliders();
        _hasReceivedEnergy = false;

        // Tentacles + navigator active during Dormant — they drain dust to build charge.
        dust?.SetTentaclesActive(true);
        cravingNavigator?.SetActive(true);

        // Stay pinned in place while charging.
        motion?.SetFrozen(true);
        motion?.SetOverrideTarget(null);

        if (_waitForDustCo == null)
            _waitForDustCo = StartCoroutine(Co_WaitForColoredDust());
    }

    private void TransitionDormantToActive()
    {
        if (_state != PhaseStarState.Dormant)
            return;

        bool zapReady =
            (_requiredZapNoteSetAvailable && _plannedEjectionDescriptor.IsValid && zappedCount >= requiredZapCount) ||
            _zapProgressState == ZapProgressState.WaitingForRetract ||
            _zapProgressState == ZapProgressState.ReadyLatched;

        if (!zapReady)
        {
            if (_tracePhaseStar)
                if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] TransitionDormantToActive blocked (not zap-ready). state={_state} zapState={_zapProgressState} zapped={zappedCount}/{requiredZapCount} descriptorValid={_plannedEjectionDescriptor.IsValid} requiredSetAvailable={_requiredZapNoteSetAvailable}", this);
            return;
        }

        StopManagedCoroutine(ref _waitForDustCo);
        _pendingDormantActivation = true;

        // If we are already latched/retracted, don't force WaitingForRetract again.
        // Re-entering WaitingForRetract here can leave the star in a non-ejectable state
        // when no additional retract event is emitted.
        bool alreadyRetracted = _zapProgressState == ZapProgressState.ReadyLatched || (dust != null && !dust.HasActiveTentacles);
        if (alreadyRetracted)
            return;

        TransitionZapState(ZapProgressState.WaitingForRetract, _requiredZapRole, "dormant-threshold-hit");
        dust?.BeginRetractionForActiveTentacles();
    }

    private void FinalizeDormantToActiveAfterRetract(bool force = false)
    {
        if ((!_pendingDormantActivation && !force) || _state != PhaseStarState.Dormant)
            return;

        _pendingDormantActivation = false;
        _state = PhaseStarState.WaitingForPoke;
        _dormantSeedVisualPrimed = false;

        // Star earned free movement after all tentacles are fully retracted.
        motion?.SetFrozen(false);
        dust?.SetTentaclesActive(false);
        cravingNavigator?.SetActive(true);

        // If paused for a sibling's node cycle, defer arming to Resume(). The state has
        // already advanced to WaitingForPoke here, so CanArm() will pass when Resume()
        // calls ArmNext(). Calling ArmNext() now would fail (CIF from the sibling's burst)
        // and cascade into HideInPlaceForBurst(), forcing an unnecessary dormant reset.
        if (_disarmReason != PhaseStarDisarmReason.SiblingActive)
            ArmNext();

        LogState("DormantWake+Armed");
    }

    private IEnumerator Co_WaitForColoredDust()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.5f);

            bool hasDust = HasColoredDustAvailable();
            bool hasDelivery = _hasReceivedEnergy;

            if (_tracePhaseStar)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] Dormant wait precheck hasDust={hasDust} hasDelivery={hasDelivery} zapped={zappedCount}/{RequiredZapCount}", this);
            }

            // Wake on stable acquisition preconditions only.
            // Ejection readiness remains gated by poke/eject code paths.
            if (hasDust && hasDelivery)
                break;
        }

        _waitForDustCo = null;
        TransitionDormantToActive();
    }

    private bool HasColoredDustAvailable()
    {
        return TryResolveContext(out _, out var dustGen) && dustGen.HasAnyDustWithRole();
    }
}
