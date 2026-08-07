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
        float vel127 = 24f)
    {
        if (role == MusicalRole.None) return;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) return;
        StartCoroutine(QuantizedDustPluckRoutine(vehicle, role, phrase01, chordSize, durTicks, vel127));
    }

    private IEnumerator QuantizedDustPluckRoutine(
        Vehicle vehicle, MusicalRole role, float phrase01, int chordSize, int durTicks, float vel127)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null || !drum.TryGetNextBaseStepDsp(out double nextStepDsp, out float stepSec, stepOffset: 1))
            yield break;

        while (AudioSettings.dspTime < nextStepDsp)
            yield return null;

        if (vehicle != null)
        {
            if (_lastVehicleDustPluckDsp.TryGetValue(vehicle, out double lastDsp) &&
                AudioSettings.dspTime - lastDsp < stepSec)
                yield break; // another cell already plucked for this vehicle this step

            _lastVehicleDustPluckDsp[vehicle] = AudioSettings.dspTime;
        }

        ResolveAndPlayDustChordPluckNow(role, phrase01, chordSize, durTicks, vel127);
    }
}
