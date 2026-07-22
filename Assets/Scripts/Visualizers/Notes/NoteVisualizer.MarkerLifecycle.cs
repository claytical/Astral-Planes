using System.Collections.Generic;
using UnityEngine;

public partial class NoteVisualizer
{
    public void RegisterCollectedMarker(InstrumentTrack track, int burstId, int step, GameObject markerGo)
    {
        if (!track || !markerGo) return;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[RegisterCollected] {track.name} burstId={burstId} step={step}, markerGo y={markerGo.transform.position.y:F1}");
        if (noteMarkers.TryGetValue((track, step), out var existing) && existing && existing.gameObject != markerGo)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"  → DESTROYING old marker at step {step}, was at y={existing.position.y:F1}");
            Destroy(existing.gameObject);
        }

        noteMarkers[(track, step)] = markerGo.transform;
        _stepBurst[(track, step)]  = burstId;

        var tag = markerGo.GetComponent<MarkerTag>();
        if (!tag) tag = markerGo.AddComponent<MarkerTag>();
        tag.track = track;
        tag.step = step;
        tag.burstId = burstId;       // ← mark ownership by this burst
        tag.ascendBurstId = burstId; // ← persist for ascension even if canonicalize neutralizes burstId later
        tag.isPlaceholder = false;   // ← lit now
        tag.isAscending = false;
        var ml = markerGo.GetComponent<MarkerLight>() ?? markerGo.AddComponent<MarkerLight>();
        ml.LightUp(track.DisplayColor);
    }


    public void ClearManualReleaseCue(Transform vehicle)
    {
        if (vehicle == null) return;
        var v = vehicle.GetComponent<Vehicle>();
        if (v == null) return;
        if (_releaseCuesByVehicle.TryGetValue(v, out var cue) && cue != null)
            Destroy(cue);
        _releaseCuesByVehicle.Remove(v);
    }

    public void BlastManualReleaseCue(Transform vehicle)
    {
        ClearManualReleaseCue(vehicle);
    }

    public void BlastManualReleaseCueFailure(Transform vehicle, Vector3 blastPos, Color roleColor)
    {
        ClearManualReleaseCue(vehicle);

        if (failureExplosionPrefab != null)
        {
            var ps = Instantiate(failureExplosionPrefab, blastPos, Quaternion.identity);
            var main = ps.main;
            main.startColor = roleColor * 0.45f;
            ps.Play();
            Destroy(ps.gameObject, main.duration + main.startLifetime.constantMax);
        }
    }

    public void PulseMarkerSpecial(InstrumentTrack track, int stepAbs)
    {
        if (track == null) return;
        if (noteMarkers == null) return;
        if (!noteMarkers.TryGetValue((track, stepAbs), out var tr) || tr == null) return;
        var ml = tr.GetComponent<MarkerLight>();
        if (ml != null) ml.LightUp(track.DisplayColor);
    }

    /// <summary>
    /// Reset all active ascension countdowns as a new phrasing (called on chord-progression swap).
    /// Keeps markers visually in place but resets their loop countdown from the current position.
    /// </summary>
    public void ResetAscensionPhrasing()
    {
        ascensionDirector?.ResetPhrasing();
    }

    public void TriggerBurstAscend(InstrumentTrack track, int burstId, int ascendLoops)
    {
        if (ascensionDirector == null) return;

        ascensionDirector.TriggerBurstAscend(
            track,
            burstId,
            GetMarkersForTrackAndBurst,
            ascendLoops,
            GetCommittedStepForMarker
        );
    }

    /// <summary>
    /// Triggers timed ascension for all markers in [stepStart, stepEnd) on the given track.
    /// Used by SuperNodeTrackNode after an instant fill to set up the race-against-time decay.
    /// Notes re-committed by the player after this call are preserved automatically via
    /// the NoteAscensionDirector's commitTimeAtStart guard.
    /// </summary>
    public void TriggerStepRangeAscend(InstrumentTrack track, int stepStart, int stepEnd, int ascendLoopsOverride)
    {
        if (ascensionDirector == null || _drum == null || track == null) return;

        ascensionDirector.TriggerBurstAscend(
            track,
            burstId: int.MinValue,
            (t, _) => GetMarkersInStepRange(t, stepStart, stepEnd),
            ascendLoopsOverride,
            GetCommittedStepForMarker
        );
    }

    private IEnumerable<GameObject> GetMarkersInStepRange(InstrumentTrack track, int stepStart, int stepEnd)
    {
        if (noteMarkers == null) yield break;
        foreach (var kvp in noteMarkers)
        {
            if (kvp.Key.Item1 != track) continue;
            int step = kvp.Key.Item2;
            if (step < stepStart || step >= stepEnd) continue;
            var go = kvp.Value?.gameObject;
            if (go != null) yield return go;
        }
    }

    /// <summary>
    /// Defensive cleanup: removes the marker at stepAbs for the given track if it is not
    /// currently in the persistent loop and is not mid-ascension. Used after a discard to
    /// ensure the authored step's marker is gone even if isPlaceholder or burstId was altered.
    /// </summary>
    public void RemoveOrphanMarkerAtStep(InstrumentTrack track, int stepAbs)
    {
        var key = (track, stepAbs);
        if (!noteMarkers.TryGetValue(key, out var tr) || tr == null) return;

        var tag = tr.GetComponent<MarkerTag>();
        if (tag != null && tag.isAscending) return; // leave ascending markers alone

        // Only remove if the step is not in the persistent loop (it was discarded, not committed).
        if (track != null && track.IsPersistentStepOccupied(stepAbs)) return;

        noteMarkers.Remove(key);
        Destroy(tr.gameObject);
    }

    /// <summary>
    /// Removes and destroys all placeholder markers for the given track and burst.
    /// Called on burst completion so authored-step placeholders that were never
    /// committed (collectables picked up and released elsewhere, or discarded) don't linger.
    /// </summary>
    public void RemoveAllPlaceholdersForBurst(InstrumentTrack track, int burstId)
    {
        var toRemove = new List<(InstrumentTrack, int)>();
        foreach (var (step, _, tag) in IterateMarkersForTrack(track,
            t => t.isPlaceholder && !t.isAscending && t.burstId == burstId))
        {
            toRemove.Add((track, step));
        }
        foreach (var key in toRemove)
        {
            if (noteMarkers.TryGetValue(key, out var tr) && tr != null)
                Destroy(tr.gameObject);
            noteMarkers.Remove(key);
        }
    }
}
