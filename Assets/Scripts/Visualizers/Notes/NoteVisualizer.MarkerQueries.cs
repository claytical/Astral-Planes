using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marker query and step-search methods for NoteVisualizer.
/// All methods here are read-mostly: they query noteMarkers or search for steps
/// without mutating marker state. Exception: SetManualReleaseCuePosition and
/// UpdateCarryHighlights drive transient visual state on markers.
/// </summary>
public partial class NoteVisualizer
{
    // Tracks scanned by the previous UpdateCarryHighlights call, so a call with an
    // unchanged carried set only has to revisit tracks that could plausibly need a
    // re-highlight (see UpdateCarryHighlights).
    private readonly HashSet<InstrumentTrack> _lastCarriedScratch = new();
    private readonly List<InstrumentTrack> _carryHighlightScanScratch = new();

    // Iterates noteMarkers for a specific track only (via the per-track index), skipping
    // null transforms and null/missing tags. Optional filter predicate applied to MarkerTag
    // before yielding.
    private IEnumerable<(int step, Transform tr, MarkerTag tag)> IterateMarkersForTrack(
        InstrumentTrack track, Func<MarkerTag, bool> filter = null)
    {
        if (!noteMarkers.TryGetTrackMap(track, out var map) || map == null) yield break;

        foreach (var kv in map)
        {
            var tr = kv.Value;
            if (!tr) continue;
            var tag = tr.GetComponent<MarkerTag>();
            if (tag == null) continue;
            if (filter != null && !filter(tag)) continue;
            yield return (kv.Key, tr, tag);
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

        if (noteMarkers.TryGetTrackMap(track, out var map) && map != null)
        {
            foreach (var kv in map)
            {
                int step = kv.Key;
                if (step < 0 || step >= effectiveTotal) continue;
                if (excludedSteps != null && excludedSteps.Contains(step)) continue;

                var tr = kv.Value;
                if (!tr) continue;
                var tag = tr.GetComponent<MarkerTag>();
                if (tag == null || !tag.isPlaceholder) continue;

                double fwd = (step - from + effectiveTotal) % effectiveTotal;
                if (fwd < bestForward) { bestForward = fwd; bestStep = step; }
            }
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

        if (noteMarkers.TryGetTrackMap(track, out var map) && map != null)
        {
            foreach (var kv in map)
            {
                int step = kv.Key;
                if (step < 0 || step >= effectiveTotal) continue;
                if (excludedSteps != null && excludedSteps.Contains(step)) continue;

                var tr = kv.Value;
                if (!tr) continue;
                var tag = tr.GetComponent<MarkerTag>();
                if (tag == null || !tag.isPlaceholder) continue;

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
        }

        if (bestStep < 0) return false;
        targetAbsStep = bestStep;
        forwardSteps = bestForward;
        return true;
    }

    // Places (creating if needed) the manual-release ghost cue at worldPos. Position/visibility
    // are owned by the caller (Vehicle.TickNoteTrail rides the vehicle's NoteTether curve) —
    // this method only manages the per-vehicle cue GameObject lifecycle. Position keeps
    // updating even while hidden so the cue is correctly placed the moment it reappears.
    public void SetManualReleaseCuePosition(Transform vehicle, Vector3 worldPos, bool visible)
    {
        if (releaseCuePrefab == null || vehicle == null) return;
        if (!isActiveAndEnabled) return;

        var v = vehicle.GetComponent<Vehicle>();
        if (v == null) return;

        if (!_releaseCuesByVehicle.TryGetValue(v, out var cue) || cue == null)
        {
            cue = Instantiate(releaseCuePrefab, worldPos, Quaternion.identity);
            _releaseCuesByVehicle[v] = cue;
        }
        else
        {
            cue.transform.position = worldPos;
        }

        var sr = cue.GetComponentInChildren<SpriteRenderer>();
        if (sr != null) sr.enabled = visible;
    }

    /// <summary>
    /// Grey-vs-available tint for placeholder markers, reflecting which tracks the calling
    /// vehicle currently carries. Called once per vehicle per frame, so with N vehicles this
    /// runs N times against the same shared marker set every frame — instead of rescanning
    /// every marker on every active track each time, only tracks that are currently carried
    /// (may have gained a fresh placeholder marker that still needs highlighting) or that were
    /// carried last call but no longer are (need resetting to grey) are visited. A track that's
    /// consistently not carried is left alone, since its placeholder markers are already grey
    /// by default at creation time.
    /// </summary>
    public void UpdateCarryHighlights(HashSet<InstrumentTrack> carried)
    {
        if (noteMarkers == null) return;

        _carryHighlightScanScratch.Clear();
        if (carried != null)
        {
            foreach (var t in carried)
                _carryHighlightScanScratch.Add(t);
        }
        foreach (var t in _lastCarriedScratch)
        {
            if (carried == null || !carried.Contains(t))
                _carryHighlightScanScratch.Add(t);
        }

        for (int i = 0; i < _carryHighlightScanScratch.Count; i++)
        {
            var track = _carryHighlightScanScratch[i];
            if (track == null) continue;
            if (!noteMarkers.TryGetTrackMap(track, out var map) || map == null) continue;

            bool isCarried = carried != null && carried.Contains(track);

            foreach (var kv in map)
            {
                var tr = kv.Value;
                if (tr == null) continue;
                var tag = tr.GetComponent<MarkerTag>();
                if (tag == null || !tag.isPlaceholder) continue;
                var ml = tag.CachedMarkerLight;
                if (ml == null) continue;

                if (isCarried) ml.SetAvailable(track.DisplayColor);
                else ml.SetGrey(track.DisplayColor);
            }
        }

        _lastCarriedScratch.Clear();
        if (carried != null)
            foreach (var t in carried) _lastCarriedScratch.Add(t);
    }

    private IEnumerable<GameObject> GetMarkersForTrackAndBurst(InstrumentTrack track, int burstId)
    {
        if (!noteMarkers.TryGetTrackMap(track, out var map) || map == null) yield break;

        foreach (var kv in map)
        {
            var tr = kv.Value;
            if (tr == null) continue;
            var tag = tr.GetComponent<MarkerTag>();
            if (tag != null && tag.burstId != burstId) continue;
            yield return tr.gameObject;
        }
    }

    // Returns the loop step this marker is committed at — the noteMarkers dictionary
    // key, which is authoritative even when tag.step has drifted (e.g. after a
    // reverse-order manual release places a note into a different placeholder slot).
    private int GetCommittedStepForMarker(InstrumentTrack track, GameObject go)
    {
        if (noteMarkers == null || go == null) return -1;
        if (!noteMarkers.TryGetTrackMap(track, out var map) || map == null) return -1;

        foreach (var kv in map)
        {
            if (kv.Value != null && kv.Value.gameObject == go)
                return kv.Key;
        }
        return -1;
    }
}
