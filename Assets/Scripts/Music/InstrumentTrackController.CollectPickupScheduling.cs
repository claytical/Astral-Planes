using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Quantizes collectable-pickup audio (the collected note itself, and its short tactile
// confirm blip) to the next step-grid DSP boundary, and throttles each per vehicle so a
// vehicle sweeping through a cluster of same-role collectables doesn't stack simultaneous
// notes. Mirrors InstrumentTrackController.DustPluckScheduling.cs's pattern. The note's
// commit to the loop (AddNoteToLoop) happens synchronously at pickup time in InstrumentTrack —
// only the audible feedback is deferred here.
public partial class InstrumentTrackController
{
    private readonly Dictionary<Vehicle, double> _lastVehicleCollectNoteDsp = new();
    private readonly Dictionary<Vehicle, double> _lastVehicleCollectConfirmDsp = new();

    public void PlayQuantizedCollectNote(
        Vehicle vehicle, InstrumentTrack track, int stepIndex, int rawMidi, int durationTicks, float velocity127)
    {
        if (track == null) return;
        StartCoroutine(QuantizedThrottledPickupRoutine(
            vehicle, _lastVehicleCollectNoteDsp,
            () => track.PlayQuantizedNoteForStep(stepIndex, rawMidi, durationTicks, velocity127)));
    }

    public void PlayQuantizedCollectConfirm(Vehicle vehicle, InstrumentTrack track, int note, float velocity)
    {
        if (track == null) return;
        StartCoroutine(QuantizedThrottledPickupRoutine(
            vehicle, _lastVehicleCollectConfirmDsp,
            () => track.PlayCollectionConfirm(note, velocity)));
    }

    private IEnumerator QuantizedThrottledPickupRoutine(
        Vehicle vehicle, Dictionary<Vehicle, double> lastFireDsp, Action fire)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null || !drum.TryGetNextBaseStepDsp(out double nextStepDsp, out float stepSec, stepOffset: 1))
            yield break;

        while (AudioSettings.dspTime < nextStepDsp)
            yield return null;

        if (vehicle != null)
        {
            if (lastFireDsp.TryGetValue(vehicle, out double lastDsp) &&
                AudioSettings.dspTime - lastDsp < stepSec)
                yield break; // another pickup already fired this sound for this vehicle this step

            lastFireDsp[vehicle] = AudioSettings.dspTime;
        }

        fire();
    }
}
