using System.Collections.Generic;
using UnityEngine;

public partial class NoteVisualizer
{
    private void UpdatePlayheadParticleTrailWorld()
    {
        if (playheadParticles == null || playheadLine == null) return;

        var main = playheadParticles.main;
        if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            main.simulationSpace = ParticleSystemSimulationSpace.World;

        Vector3 now = playheadParticles.transform.position;

        // First frame after enable/reset: just seed the position.
        if (!_hasLastPlayheadParticleWorldPos)
        {
            _hasLastPlayheadParticleWorldPos = true;
            _lastPlayheadParticleWorldPos = now;
            return;
        }

        Vector3 prev = _lastPlayheadParticleWorldPos;
        if (!float.IsNaN(playheadTrailWorldZOverride))
            prev.z = playheadTrailWorldZOverride;

        float dist = Vector3.Distance(prev, now);
        if (dist <= 0.00001f)
            return;

        // Emit a number of particles proportional to distance traveled.
        int emitCount = Mathf.Clamp(
            Mathf.CeilToInt(dist * playheadTrailEmitPerWorldUnit),
            1,
            playheadTrailMaxEmitPerFrame
        );

        // Emit evenly along the traveled segment so the trail is continuous.
        var emitParams = new ParticleSystem.EmitParams();
        for (int i = 0; i < emitCount; i++)
        {
            float u = (emitCount <= 1) ? 1f : (i / (emitCount - 1f));
            Vector3 p = Vector3.Lerp(prev, now, u);
            emitParams.position = p;

// Make trail particles inherit the same pulse color immediately.
            if (_releasePulseT > 0f)
            {
                float pulse01 = Mathf.Clamp01(_releasePulseT / Mathf.Max(0.0001f, releasePulseSeconds));
                emitParams.startColor = Color.Lerp(Color.white, GetReleasePulseColor(_lastReleasePulseRole), .5f);
            }
            else
            {
                emitParams.startColor = Color.white; // or omit if you want the system default
            }


            playheadParticles.Emit(emitParams, 1);
        }

        _lastPlayheadParticleWorldPos = now;
    }

    /// <summary>
    /// Set how "charged" the playhead is [0..1] based on how many notes in the
    /// current burst have been collected. This will be smoothed visually.
    /// </summary>
    public void SetPlayheadEnergy01(float value)
    {
        _playheadEnergyTarget01 = Mathf.Clamp01(value);
    }

    /// <summary>
    /// Called when a burst completes & drums change to trigger a short visual pulse.
    /// </summary>
    public void TriggerPlayheadReleasePulse(MusicalRole role = MusicalRole.None)
    {
        _lastReleasePulseRole = role;
        _pendingReleasePulse = true;
        _releasePulseT = releasePulseSeconds; // start the color pulse immediately
    }

    private Color GetReleasePulseColor(MusicalRole role)
    {
        if (role != MusicalRole.None)
        {
            var profile = MusicalRoleProfileLibrary.GetProfile(role);
            if (profile != null) return profile.GetBaseColor();
        }
        return releasePulseColorFallback;
    }

    // Visual clock MUST match the audio clock: use leader loop length for both x-position and step sampling.
    // GetLeaderSteps() returns the expanded step count (e.g. 32 for a 2-bin loop).
    private void MovePlayheadLine(double leaderStartDsp)
    {
        float fullVisualLoopDuration = Mathf.Max(0.0001f, _drum.GetLoopLengthInSeconds());
        float globalElapsed = (float)(AudioSettings.dspTime - leaderStartDsp);
        float globalNormalized = (globalElapsed % fullVisualLoopDuration) / fullVisualLoopDuration;
        float xPos = Mathf.Lerp(0f, GetScreenWidth(), Mathf.Clamp01(globalNormalized));
        playheadLine.anchoredPosition = new Vector2(xPos, playheadLine.anchoredPosition.y);
    }

    private void ComputeCurrentStepState(double leaderStartDsp,
        out int currentStep, out bool shimmer, out float maxVelocity)
    {
        int drumTotalSteps = Mathf.Max(1, _drum.GetLeaderSteps());
        float fullVisualLoopDuration = Mathf.Max(0.0001f, _drum.GetLoopLengthInSeconds());
        float stepDuration = fullVisualLoopDuration / drumTotalSteps;
        float leaderT = (float)((AudioSettings.dspTime - leaderStartDsp) % fullVisualLoopDuration);
        if (leaderT < 0f) leaderT += fullVisualLoopDuration;

        currentStep = Mathf.FloorToInt(leaderT / stepDuration);
        currentStep = ((currentStep % drumTotalSteps) + drumTotalSteps) % drumTotalSteps;

        shimmer = false;
        maxVelocity = 0f;
        var controller = _gfm?.controller;
        if (controller?.tracks == null) return;
        foreach (var track in controller.tracks)
        {
            if (track == null) continue;
            maxVelocity = Mathf.Max(maxVelocity, track.GetVelocityAtStep(currentStep) / 127f);
            if (!shimmer && _ghostNoteSteps.TryGetValue(track, out var steps)
                && steps != null && steps.Contains(currentStep))
                shimmer = true;
        }
    }

    private void UpdateParticleEmission(bool shimmer, float maxVelocity)
    {
        if (playheadParticles == null) return;

        var main = playheadParticles.main;
        var emission = playheadParticles.emission;
        float velFactor = Mathf.Clamp01(maxVelocity);
        float energyFactor = Mathf.Lerp(0.3f, 1.0f, _playheadEnergy01);
        float chargeFactor = 1.0f + 1.5f * _lineCharge01;

        main.startSize = Mathf.Lerp(0.8f, 1.2f, velFactor) * energyFactor;
        emission.rateOverTime = Mathf.Lerp(10f, 50f, velFactor) * energyFactor * chargeFactor;
        emission.enabled = shimmer || _lineCharge01 > 0.05f || _playheadEnergy01 > 0.05f;

        if (_releasePulseT > 0f)
            _releasePulseT = Mathf.Max(0f, _releasePulseT - Time.deltaTime);

        var col = playheadParticles.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(stepColor, 0f), new GradientColorKey(stepColor, 1f) },
            new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0.0f, 1f) }
        );
        col.color = g;

        if (_pendingReleasePulse)
        {
            _pendingReleasePulse = false;
            playheadParticles.Emit(30);
            _playheadEnergyTarget01 = 0f;
        }
    }

    private Color ComputeStepColor(int step)
    {
        var controller = _gfm != null ? _gfm.controller : null;
        if (controller == null || controller.tracks == null) return Color.white;

        float totalW = 0f;
        Vector3 sum = Vector3.zero;

        foreach (var tr in controller.tracks)
        {
            if (tr == null) continue;

            float v = tr.GetVelocityAtStep(step);   // 0..127
            if (v <= 0f) continue;

            float w = v / 127f;
            totalW += w;

            Color c = tr.DisplayColor;
            sum += new Vector3(c.r, c.g, c.b) * w;
        }

        if (totalW <= 0.0001f) return Color.white;

        Vector3 rgb = sum / totalW;
        return new Color(rgb.x, rgb.y, rgb.z, 1f);
    }

    public void MarkGhostPadding(InstrumentTrack track, int startStepInclusive, int count) {
        if (!_ghostNoteSteps.TryGetValue(track, out var set))
            _ghostNoteSteps[track] = set = new HashSet<int>();

        int leaderBins = (_ctrl != null) ? Mathf.Max(1, _ctrl.GetCommittedLeaderBins()) : 1;
        int binSize    = (_drum != null) ? Mathf.Max(1, _drum.totalSteps) : 16;
        int total      = Mathf.Max(1, leaderBins * binSize);

        for (int i = 0; i < count; i++)
            set.Add((startStepInclusive + i) % total);
    }

    private bool RefreshCoreRefs(bool force = false)
    {
        // Cache the singleton once (still cheap, but avoid repeating per-frame)
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm == null) return false;

        // Pull current references from GFM
        var newDrum = _gfm.activeDrumTrack;
        var newCtrl = _gfm.controller;

        // If we have never bound, or something changed (scene swap / re-register), update caches.
        if (force || _drum != newDrum || _ctrl != newCtrl)
        {
            _drum = newDrum;
            _ctrl = newCtrl;
        }
        ascensionDirector?.Initialize(_drum);

        return (_drum != null && _ctrl != null && _ctrl.tracks != null);
    }

    // Returns false if no usable anchor is available (Update should return early).
    // Writes the resolved anchor DSP time into leaderStartDsp.
    private bool TickAnchorGuard(out double leaderStartDsp)
    {
        leaderStartDsp =
            (_drum.leaderStartDspTime > 0.0) ? _drum.leaderStartDspTime :
            (_drum.startDspTime > 0.0)       ? _drum.startDspTime :
                                               0.0;

        if (leaderStartDsp <= 0.0)
        {
            if (!_hasCachedDrumAnchor)
                return false;
            leaderStartDsp = _cachedLeaderStartDspTime;
        }
        else
        {
            _hasCachedDrumAnchor = true;
            _cachedLeaderStartDspTime = leaderStartDsp;
        }
        return true;
    }

    private void TickPlayheadEnergy()
    {
        _playheadEnergy01 = Mathf.MoveTowards(
            _playheadEnergy01,
            _playheadEnergyTarget01,
            _playheadEnergyLerpSpeed * Time.deltaTime
        );
    }

    private float GetScreenWidth() {
        RectTransform rt = _worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }
}
