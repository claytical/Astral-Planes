using UnityEngine;

public partial class PhaseStar
{
    private enum VisualMode
    {
        Bright,
        Dim,
        Hidden
    }

    private float _accumulatorRotAngle;
    private float _accumulatorDrainTimer;

    // Computes this frame's body rotation from the dust subcomponent's lock/drain state.
    // Called from Update() after _accumulatorRotAngle advances. Tentacles on either tip pair
    // (diamond index 0 or 1) keep the drain-sway timer running; only index 0 steers the angle.
    private float ComputeDiamondRotation(float dt)
    {
        if (dust == null) return _accumulatorRotAngle;

        dust.GetDiamondLockState(0, out bool aLocked, out float aLockDeg, out bool aDraining);
        dust.GetDiamondLockState(1, out _, out _, out bool bDraining);

        if (aDraining || bDraining)
            _accumulatorDrainTimer += dt;
        else
            _accumulatorDrainTimer = 0f;

        float sway = Mathf.Sin(_accumulatorDrainTimer * drainTiltSpeed * Mathf.PI * 2f) * drainTiltDeg;
        return aLocked ? aLockDeg + (aDraining ? sway : 0f) : _accumulatorRotAngle;
    }

    // Handles latched-but-dormant recovery and pending-activation finalization each frame.
    private void UpdateStateRecovery()
    {
        if (_state == PhaseStarState.Dormant &&
            _zapProgressState == ZapProgressState.ReadyLatched &&
            !_pendingDormantActivation)
        {
            TransitionDormantToActive();
        }

        if (_pendingDormantActivation && (dust == null || !dust.HasActiveTentacles))
            FinalizeDormantToActiveAfterRetract();
    }

    // Keeps the preview role/track pinned to the star's attuned role, refreshes the planned
    // ejection descriptor when the track changes, and updates the charge display lerp.
    private void UpdateDominantRole(float dt)
    {
        if (_attunedRole != MusicalRole.None)
        {
            if (_attunedRole != _previewRole)
            {
                _previewRole = _attunedRole;
                _cachedTrack = FindTrackByRole(_attunedRole);
                _previewColor = ResolveRoleColor(_attunedRole, _cachedTrack);
                if (zappedCount == 0)
                    TryRefreshRequiredZapCountForPlannedRole(_attunedRole, _cachedTrack, resetCurrentZapCount: false, reason: "attuned-role-switch");
            }
            else
            {
                InstrumentTrack latestTrack = FindTrackByRole(_attunedRole);
                bool trackChanged = !ReferenceEquals(latestTrack, _plannedEjectionDescriptor.track);
                if (trackChanged)
                {
                    _cachedTrack = latestTrack;
                    if (zappedCount == 0)
                        TryRefreshRequiredZapCountForPlannedRole(_attunedRole, latestTrack, resetCurrentZapCount: false, reason: "track-availability-change");
                }
            }

            _displayedCharge01 = Mathf.Lerp(_displayedCharge01, ZapProgress01, dt * chargeDisplayLerpSpeed);
        }
        else
        {
            _previewRole = MusicalRole.None;
            _displayedCharge01 = Mathf.Lerp(_displayedCharge01, 0f, dt * chargeDisplayLerpSpeed);
        }
    }

    // Drives the star body (count diamonds, PhaseStar.DiamondBody.cs) and the body/particle colour
    // each frame. Must be called after UpdateDominantRole() and ComputeDiamondRotation().
    private void UpdateChargeVisuals(float dt, float rotA)
    {
        // A sleeping star (blocked by work elsewhere, PhaseStar.PokeFeedback.cs) never shows
        // the fast ready-spin, even when its own zaps are met.
        bool dominantReady = IsEjectionReady() && !_sleepVisual;

        // Paused (SiblingActive) stars keep updating too — Sleeping desaturates them and the
        // rotation runs slowed (PhaseStar.PokeFeedback.cs), instead of freezing mid-frame.
        UpdateStarBody(dt, rotA, dominantReady);

        if (visuals == null || _burstOffScreen || _sleepVisual || _disarmReason == PhaseStarDisarmReason.SiblingActive)
            return;

        // The body is now sized by its count, never by charge: the root stays at full scale.
        if (visuals.transform.localScale != Vector3.one) visuals.transform.localScale = Vector3.one;

        bool charging = _state == PhaseStarState.Dormant ||
                        (_state == PhaseStarState.WaitingForPoke && ZapProgress01 < 1f);
        if (charging) visuals.ToggleShardRenderers(true);

        Color bodyColor = _previewRole != MusicalRole.None ? _previewColor : Color.gray;
        visuals.LerpBodyColor(bodyColor, _displayedCharge01);
    }

    private void EnsureDormantSeedVisuals()
    {
        if (visuals == null) return;
        if (_state != PhaseStarState.Dormant) return;
        if (_burstOffScreen) return;
        if (_disarmReason == PhaseStarDisarmReason.SiblingActive) return;

        if (!_dormantSeedVisualPrimed)
        {
            visuals.ShowDim(ResolvePreviewColorByReadiness());
            _dormantSeedVisualPrimed = true;
        }

    }

    private void SetVisual(VisualMode mode, Color tint)
    {
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!visuals) return;
        switch (mode)
        {
            case VisualMode.Bright: visuals.ShowBright(tint); break;
            case VisualMode.Dim: visuals.ShowDim(tint); break;
            case VisualMode.Hidden: visuals.HideAll(); break;
        }
    }
}
