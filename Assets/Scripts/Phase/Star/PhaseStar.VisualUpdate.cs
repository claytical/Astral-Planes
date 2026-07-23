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

    // Computes this frame's diamond rotation angles and lock state from the dust
    // subcomponent's drain state. Called from Update() after _accumulatorRotAngle advances.
    private void ComputeDiamondRotation(float dt, out float rotA, out bool aLocked, out float rotB, out bool bLocked)
    {
        aLocked = false;
        bLocked = false;

        if (dust != null)
        {
            dust.GetDiamondLockState(0, out aLocked, out float aLockDeg, out bool aDraining);
            dust.GetDiamondLockState(1, out bLocked, out float bLockDeg, out bool bDraining);

            if (aDraining || bDraining)
                _accumulatorDrainTimer += dt;
            else
                _accumulatorDrainTimer = 0f;

            float sway = Mathf.Sin(_accumulatorDrainTimer * drainTiltSpeed * Mathf.PI * 2f) * drainTiltDeg;
            rotA = aLocked ? aLockDeg + (aDraining ? sway : 0f) : _accumulatorRotAngle;
            rotB = bLocked ? bLockDeg + (bDraining ? sway : 0f) : -_accumulatorRotAngle;
        }
        else
        {
            rotA = _accumulatorRotAngle;
            rotB = -_accumulatorRotAngle;
        }
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
                visuals?.ResetDualDiamondVisualState();
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

    // Drives diamond rendering and the charge-scale/body-color visuals each frame.
    // Must be called after UpdateDominantRole() and after diamond rotation angles are computed.
    // Note: UpdateDualDiamonds resets diamond localScale to 1, so the charge-scale block
    // below must run after it to win for the Dormant phase.
    private void UpdateChargeVisuals(float rotA, bool aLocked, float rotB, bool bLocked)
    {
        bool dominantReady = IsEjectionReady();

        if (_previewVisual != null && _disarmReason != PhaseStarDisarmReason.SiblingActive)
        {
            visuals?.UpdateDualDiamonds(
                _previewColor,
                _displayedCharge01,
                rotA, aLocked,
                rotB, bLocked,
                dominantReady,
                ReadyRotSpeedMul);
        }

        // Scale 0→1 as charge builds. _previewVisual and _previewVisualB are children of
        // PhaseStar (not of visuals), so they must be scaled explicitly here.
        // Sqrt curve: front-loads growth so small charge values are perceptible.
        // e.g. 10% charge → 32% scale, 25% charge → 50% scale, 100% → 100%.
        if ((_state == PhaseStarState.Dormant ||
             (_state == PhaseStarState.WaitingForPoke && ZapProgress01 < 1f))
            && !_burstOffScreen)
        {
            float visualScale01 = Mathf.Max(dormantSeedScale, Mathf.Sqrt(_displayedCharge01));
            if (dust != null && dust.HasActiveTentacles)
                visualScale01 = Mathf.Max(visualScale01, tentacleBloomMinScale);

            Vector3 chargeScale = Vector3.one * visualScale01;
            if (visuals != null) visuals.transform.localScale = chargeScale;
            if (_previewVisual != null)  _previewVisual.localScale  = chargeScale;
            if (_previewVisualB != null) _previewVisualB.localScale = chargeScale;

            if (visualScale01 > 0.001f && _disarmReason != PhaseStarDisarmReason.SiblingActive)
            {
                visuals?.ToggleShardRenderers(true);
                Color roleColor = _previewRole != MusicalRole.None ? _previewColor : Color.gray;
                visuals?.LerpBodyColor(roleColor, _displayedCharge01);
            }
        }
        else if (visuals != null && !_burstOffScreen && _disarmReason != PhaseStarDisarmReason.SiblingActive)
        {
            Color bodyColor = _previewRole != MusicalRole.None ? _previewColor : Color.gray;
            visuals.LerpBodyColor(bodyColor, _displayedCharge01);
        }
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

        float minScale = Mathf.Max(0f, dormantSeedScale);
        if (visuals.transform.localScale.x < minScale)
            visuals.transform.localScale = Vector3.one * minScale;
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
