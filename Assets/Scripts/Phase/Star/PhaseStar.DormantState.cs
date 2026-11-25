using System.Collections;
using UnityEngine;

public partial class PhaseStar
{
    private bool _pendingDormantActivation;
    private Coroutine _waitForDustCo;
    private Coroutine _tentacleActivationCo;

    /// <param name="worldPos">Where the star settles (its spawn cell).</param>
    /// <param name="entryFrom">Optional off-screen start: the star swims in from here to
    /// <paramref name="worldPos"/> (PhaseStarMotion2D.BeginEntrance) before it reaches for flowers.</param>
    public void EnterInMaze(Vector2 worldPos, Vector2? entryFrom = null)
    {
        EnsureSubcomponents();
        StopManagedCoroutine(ref _waitForDustCo);
        StopManagedCoroutine(ref _tentacleActivationCo);

        _state = PhaseStarState.Dormant;

        Vector2 startPos = entryFrom ?? worldPos;
        transform.position = (Vector3)startPos + Vector3.forward * transform.position.z;
        var rb = motion != null ? motion.GetComponent<Rigidbody2D>() : GetComponent<Rigidbody2D>();
        if (rb != null) rb.position = startPos;

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
            // Before Enable(): Enable() soft-contains the star on screen, which an entrance skips.
            if (entryFrom.HasValue) motion.BeginEntrance(worldPos);
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

        // No seed body: a charging star is its tentacles until notes form diamonds (DiamondBody.cs).
        if (visuals != null) visuals.transform.localScale = Vector3.one;
        visuals?.ShowDim(ResolvePreviewColorByReadiness());
        _dormantSeedVisualPrimed = true;

        // Colliders stay on for dust/world contact, but PhaseStarMotion2D's ghost drift
        // ignores vehicle collisions while Dormant (ships pass through and get buffeted).
        EnableColliders();
        _hasReceivedEnergy = false;

        // Navigator active immediately, but tentacles wait a beat so the star
        // reads as appearing before it starts reaching for dust.
        StopManagedCoroutine(ref _tentacleActivationCo);
        _tentacleActivationCo = StartCoroutine(Co_ActivateTentaclesAfterDelay());
        cravingNavigator?.SetActive(true);

        // No self-steering while charging — ghost drift (PhaseStarMotion2D) wanders it slowly.
        motion?.SetFrozen(true);
        motion?.SetOverrideTarget(null);

        if (_waitForDustCo == null)
            _waitForDustCo = StartCoroutine(Co_WaitForColoredDust());
    }

    private IEnumerator Co_ActivateTentaclesAfterDelay()
    {
        if (tentacleActivationDelay > 0f)
            yield return new WaitForSeconds(tentacleActivationDelay);
        // Finish swimming in before reaching for flowers (buds still show during the entrance).
        while (motion != null && motion.IsEntering && _state == PhaseStarState.Dormant)
            yield return null;

        _tentacleActivationCo = null;
        if (_state != PhaseStarState.Dormant) yield break;
        dust?.SetTentaclesActive(true);
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
        StopManagedCoroutine(ref _tentacleActivationCo);
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
        if (!TryResolveContext(out _, out var dustGen)) return false;
        // O(1): reads the shared once-per-frame flower scan.
        return _attunedRole != MusicalRole.None
            ? dustGen.FlowerCount(_attunedRole) > 0
            : dustGen.HasAnyDustWithRole();
    }
}
