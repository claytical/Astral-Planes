using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marker query and step-search methods for NoteVisualizer.
/// All methods here are read-mostly: they query noteMarkers or search for steps
/// without mutating marker state. Exception: UpdateManualReleaseCueExcluding and
/// UpdateCarryHighlights drive transient visual state on markers.
/// </summary>
public partial class NoteVisualizer
{
    // Iterates noteMarkers for a specific track, skipping null transforms and null/missing tags.
    // Optional filter predicate applied to MarkerTag before yielding.
    private IEnumerable<(int step, Transform tr, MarkerTag tag)> IterateMarkersForTrack(
        InstrumentTrack track, Func<MarkerTag, bool> filter = null)
    {
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 != track) continue;
            var tr = kv.Value;
            if (!tr) continue;
            var tag = tr.GetComponent<MarkerTag>();
            if (tag == null) continue;
            if (filter != null && !filter(tag)) continue;
            yield return (kv.Key.Item2, tr, tag);
        }
    }

    // Computes the effective loop domain shared by both step-search methods.
    // loopMultiplier can expand the playable timeline beyond the caller-provided totalAbsSteps,
    // so 'from' and all wrap math must normalize against effectiveTotal, not totalAbsSteps.
    private bool TryComputeLoopDomain(InstrumentTrack track, int totalAbsSteps,
        double rawAbsStep, out int effectiveTotal, out double from)
    {
        effectiveTotal = Mathf.Max(totalAbsSteps,
            track.loopMultiplier * (_drum != null ? _drum.totalSteps : track.BinSize()));
        from = rawAbsStep % effectiveTotal;
        if (from < 0) from += effectiveTotal;
        return noteMarkers != null && noteMarkers.Count > 0;
    }

    public bool TryGetNextUnlitStepExcluding(
        InstrumentTrack track, double rawAbsStep, int totalAbsSteps,
        HashSet<int> excludedSteps, out int targetAbsStep)
    {
        targetAbsStep = -1;
        if (track == null) return false;
        if (!TryComputeLoopDomain(track, Mathf.Max(1, totalAbsSteps), rawAbsStep,
            out int effectiveTotal, out double from)) return false;

        int bestStep = -1;
        double bestForward = double.MaxValue;

        foreach (var (step, _, _) in IterateMarkersForTrack(track, t => t.isPlaceholder))
        {
            if (step < 0 || step >= effectiveTotal) continue;
            if (excludedSteps != null && excludedSteps.Contains(step)) continue;

            double fwd = (step - from + effectiveTotal) % effectiveTotal;
            if (fwd < bestForward) { bestForward = fwd; bestStep = step; }
        }

        if (bestStep < 0) return false;
        targetAbsStep = bestStep;
        return true;
    }

    public bool TryGetNearestUnlitStepExcluding(
        InstrumentTrack track, double rawAbsStep, int totalAbsSteps,
        HashSet<int> excludedSteps, out int targetAbsStep, out double forwardSteps)
    {
        targetAbsStep = -1;
        forwardSteps = double.MaxValue;
        if (track == null) return false;
        if (!TryComputeLoopDomain(track, Mathf.Max(1, totalAbsSteps), rawAbsStep,
            out int effectiveTotal, out double from)) return false;

        int bestStep = -1;
        double bestDistance = double.MaxValue;
        double bestForward = double.MaxValue;

        foreach (var (step, _, _) in IterateMarkersForTrack(track, t => t.isPlaceholder))
        {
            if (step < 0 || step >= effectiveTotal) continue;
            if (excludedSteps != null && excludedSteps.Contains(step)) continue;

            double fwd = (step - from + effectiveTotal) % effectiveTotal;
            double back = (from - step + effectiveTotal) % effectiveTotal;
            double dist = Math.Min(fwd, back);

            if (dist < bestDistance || (Math.Abs(dist - bestDistance) < 0.0001 && fwd < bestForward))
            {
                bestDistance = dist;
                bestForward = fwd;
                bestStep = step;
            }
        }

        if (bestStep < 0) return false;
        targetAbsStep = bestStep;
        forwardSteps = bestForward;
        return true;
    }

    public void UpdateManualReleaseCueExcluding(
        Transform vehicle, InstrumentTrack track,
        double rawAbsStep, int floorAbsStep, int totalAbsSteps,
        HashSet<int> excludedSteps)
    {
        if (releaseCuePrefab == null || vehicle == null || track == null) return;
        if (!isActiveAndEnabled) return;

        totalAbsSteps = Mathf.Max(1, totalAbsSteps);

        if (!TryGetNextUnlitStepExcluding(track, rawAbsStep, totalAbsSteps, excludedSteps, out int targetAbs))
        {
            ClearManualReleaseCue(vehicle);
            return;
        }

        Vector3 a = vehicle.position;
        Vector3 b = (noteMarkers != null &&
                     noteMarkers.TryGetValue((track, targetAbs), out var markerTr) && markerTr != null)
            ? markerTr.position : a;

        double fwd = (targetAbs - rawAbsStep + totalAbsSteps) % totalAbsSteps;
        int binSize = (_ctrl != null && _drum != null) ? Mathf.Max(1, _drum.totalSteps) : 16;
        int lookahead = Mathf.Clamp(Mathf.Max(releaseCueLookaheadSteps, binSize), 1, totalAbsSteps);

        if (fwd > lookahead) { ClearManualReleaseCue(vehicle); return; }

        float u = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01((float)(fwd / lookahead)));
        Vector3 p = Vector3.Lerp(a, b, u);
        if (releaseCueArcHeight != 0f) p.y += releaseCueArcHeight * 4f * u * (1f - u);

        int id = vehicle.GetInstanceID();
        if (!_releaseCuesByVehicle.TryGetValue(id, out var cue) || cue == null)
        {
            _releaseCuesByVehicle[id] = cue;
        }
        else
        {
            cue.transform.position = p;
        }
    }

    public void UpdateCarryHighlights(HashSet<InstrumentTrack> carried)
    {
        if (noteMarkers == null) return;
        foreach (var kv in noteMarkers)
        {
            if (kv.Value == null) continue;
            var tag = kv.Value.GetComponent<MarkerTag>();
            if (tag == null || !tag.isPlaceholder) continue;
            var ml = kv.Value.GetComponent<MarkerLight>();
            if (ml == null) continue;
            var track = kv.Key.Item1;
            if (carried != null && carried.Contains(track))
                ml.SetAvailable(track.DisplayColor);
            else
                ml.SetGrey(track.DisplayColor);
        }
    }

    private IEnumerable<GameObject> GetMarkersForTrackAndBurst(InstrumentTrack track, int burstId)
    {
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 != track) continue;
            if (kv.Value == null) continue;
            var tag = kv.Value.GetComponent<MarkerTag>();
            if (tag != null && tag.burstId != burstId) continue;
            yield return kv.Value.gameObject;
        }
    }

    // Returns the loop step this marker is committed at — the noteMarkers dictionary
    // key, which is authoritative even when tag.step has drifted (e.g. after a
    // reverse-order manual release places a note into a different placeholder slot).
    private int GetCommittedStepForMarker(InstrumentTrack track, GameObject go)
    {
        if (noteMarkers == null || go == null) return -1;
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 != track) continue;
            if (kv.Value != null && kv.Value.gameObject == go)
                return kv.Key.Item2;
        }
        return -1;
    }
}
