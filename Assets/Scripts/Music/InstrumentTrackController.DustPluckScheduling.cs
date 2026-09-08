using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Quantizes dust-collision plucks to the next step-grid DSP boundary and throttles them so
// at most one pluck sounds per vehicle per step, even when several dust cells resolve to the
// same boundary at once. Entry point called from CosmicDust.Collision; actual chord/pitch
// resolution is deferred to ResolveAndPlayDustChordPluckNow (InstrumentTrackController.DustChordVoicing.cs).
public partial class InstrumentTrackController
{
    private readonly Dictionary<Vehicle, double> _lastVehicleDustPluckDsp = new();

    public void PlayDustChordPluck(
        Vehicle vehicle,
        MusicalRole role,
        float phrase01 = 0f,
        int chordSize = 4,
        int durTicks = 180,
        float vel127 = 24f,
        float minGapSeconds = 0f)
    {
        if (role == MusicalRole.None) return;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) return;
        StartCoroutine(QuantizedDustPluckRoutine(vehicle, role, phrase01, chordSize, durTicks, vel127, minGapSeconds));
    }

    private IEnumerator QuantizedDustPluckRoutine(
        Vehicle vehicle, MusicalRole role, float phrase01, int chordSize, int durTicks, float vel127, float minGapSeconds)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null || !drum.TryGetNextBaseStepDsp(out double nextStepDsp, out float stepSec, stepOffset: 1))
            yield break;

        while (AudioSettings.dspTime < nextStepDsp)
            yield return null;

        if (vehicle != null)
        {
            // minGapSeconds (the caller's actual computed cooldown) makes this per-vehicle gate
            // authoritative over any one cell's own cooldown, closing the race where several
            // adjacent cells touching the same vehicle independently re-arm on staggered clocks.
            // Mathf.Max keeps the original same-step dedup behavior when minGapSeconds is 0.
            float gapSec = Mathf.Max(stepSec, minGapSeconds);
            if (_lastVehicleDustPluckDsp.TryGetValue(vehicle, out double lastDsp) &&
                AudioSettings.dspTime - lastDsp < gapSec)
                yield break; // another cell already plucked for this vehicle within its cooldown window

            _lastVehicleDustPluckDsp[vehicle] = AudioSettings.dspTime;
        }

        ResolveAndPlayDustChordPluckNow(role, phrase01, chordSize, durTicks, vel127);
    }
}
