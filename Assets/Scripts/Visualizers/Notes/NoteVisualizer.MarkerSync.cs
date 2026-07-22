using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class NoteVisualizer
{
   public void ForceSyncMarkersToPersistentLoop(InstrumentTrack track)
{
    if (track == null) return;
    if (_ctrl == null || _ctrl.tracks == null) return;

    int trackIndex = Array.IndexOf(_ctrl.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

    int totalSteps = Mathf.Max(1, track.GetTotalSteps());

    // Build authoritative set of loop-owned steps (what should exist visually).
    var loopNotes = track.GetPersistentLoopNotes();
    var loopSteps = new HashSet<int>();
    if (loopNotes != null)
    {
        foreach (var (step, _, _, _, _) in loopNotes)
        {
            if (step >= 0 && step < totalSteps)
                loopSteps.Add(step);
        }
    }

    // 1) Remove stale dictionary markers (steps no longer in the persistent loop OR out of range).
    //    Do NOT destroy ascending markers mid-flight.
    if (noteMarkers != null)
    {
        var keys = noteMarkers.Keys.ToList();
        foreach (var key in keys)
        {
            if (key.Item1 != track) continue;

            int step = key.Item2;

            bool outOfRange = (step < 0 || step >= totalSteps);
            bool notInLoop  = !loopSteps.Contains(step);

            if (!outOfRange && !notInLoop) continue;

            if (noteMarkers.TryGetValue(key, out var tr) && tr != null)
            {
                var tag = tr.GetComponent<MarkerTag>();
                if (tag != null && tag.isAscending)
                    continue; // keep in-flight ascension markers intact

                // Safe to destroy (orphan / out-of-window)
                SafeDestroy(tr.gameObject);
            }

            noteMarkers.Remove(key);
        }
    }

    // 2) Remove stale row children that are loop-owned but not present in the dictionary anymore.
    //    This catches any UI stragglers not tracked in noteMarkers.
    var row = trackRows[trackIndex];
    for (int i = row.childCount - 1; i >= 0; i--)
    {
        var child = row.GetChild(i);
        if (!child) continue;

        var tag = child.GetComponent<MarkerTag>();
        if (tag == null) continue;
        if (tag.track != track) continue;
        if (tag.isAscending) continue;

        int step = tag.step;

        bool outOfRange = (step < 0 || step >= totalSteps);
        bool notInLoop  = !loopSteps.Contains(step);

        // IMPORTANT: only hard-remove "loop-owned" markers here.
        // Burst-owned markers (burstId >= 0) are governed by burst cleanup logic.
        bool loopOwned = tag.burstId < 0;

        if ((outOfRange || notInLoop) && loopOwned)
        {
            SafeDestroy(child.gameObject);
        }
    }

    // 3) Ensure every loop step has a marker (re-add missing ones).
    foreach (int step in loopSteps)
        PlacePersistentNoteMarker(track, step, lit: true, burstId: -1);

    // 4) Relayout after removals/additions.
    RecomputeTrackLayout(track);
}

    public void CanonicalizeTrackMarkers(InstrumentTrack track, int currentBurstId)
    {
        if (track == null) return;

        int trackIndex = Array.IndexOf(_ctrl.tracks, track);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId}");
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
        var row = trackRows[trackIndex];

        var loopSteps = new HashSet<int>(track.GetPersistentLoopNotes().Select(n => n.Item1));

        RemoveStaleMarkerEntries(track);
        NormalizeTagsOnRow(row, track, loopSteps, currentBurstId);
        PruneStaleMarkerDictEntries(track);

        RecomputeTrackLayout(track);
        int activeBurst = (currentBurstId >= 0) ? currentBurstId : track.currentBurstId;
        DestroyOrphanRowMarkers(track, activeBurst);
    }

    private void RemoveStaleMarkerEntries(InstrumentTrack track)
    {
        var toRemove = new List<(InstrumentTrack, int)>();
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 == track && (kv.Value == null || kv.Value.gameObject == null))
                toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Remove {k.Item1}");
            noteMarkers.Remove(k);
        }
    }

    private void NormalizeTagsOnRow(RectTransform row, InstrumentTrack track, HashSet<int> loopSteps, int currentBurstId)
    {
        var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive: true);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Tags: {tags.Length}");

        foreach (var tag in tags)
        {
            if (!tag || tag.track != track) continue;
            if (tag.isAscending) continue;

            bool isLoop = loopSteps.Contains(tag.step);
            bool inFilledBin = SafeIsStepInFilledBin(track, tag.step);
            if (!isLoop && tag.isPlaceholder)
            {
                // Keep placeholders that belong to this canonicalization burst even if bin
                // isn’t filled yet — prevents just-placed expansion markers from being
                // destroyed immediately (which broke NoteTether end targets).
                if (tag.burstId == currentBurstId)
                {
                    var key = (track, tag.step);
                    noteMarkers[key] = tag.transform;
                    var ml = tag.GetComponent<MarkerLight>() ?? tag.gameObject.AddComponent<MarkerLight>();
                    ml.SetGrey(track.DisplayColor);
                    continue;
                }

                if (!inFilledBin)
                {
                    SafeDestroy(tag.gameObject);
                    continue;
                }
            }
            if (isLoop)
            {
                // Density-injection guard: placeholder for current burst whose step is
                // already committed by a previous burst — preserve placeholder so Vehicle
                // can still target it for release.
                if (tag.isPlaceholder && tag.burstId == currentBurstId)
                {
                    noteMarkers[(track, tag.step)] = tag.transform;
                    continue;
                }

                tag.isPlaceholder = false;
                var key = (track, tag.step);
                noteMarkers[key] = tag.transform;
                continue;
            }

            if (tag.isPlaceholder)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name}");

                if (tag.burstId != currentBurstId)
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name} BurstID is not Current BurstID");
                    SafeDestroy(tag.gameObject);
                    continue;
                }

                tag.burstId = currentBurstId;
                var key = (track, tag.step);
                noteMarkers[key] = tag.transform;
            }
            else
            {
                // Non-placeholder not in loop — neutralize to loop for safety, don’t destroy.
                tag.burstId = -1;
                tag.isPlaceholder = false;
                var key = (track, tag.step);
                noteMarkers[key] = tag.transform;
            }
        }
    }

    private void PruneStaleMarkerDictEntries(InstrumentTrack track)
    {
        var toRemove = new List<(InstrumentTrack, int)>();
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 != track) continue;
            if (kv.Value == null || kv.Value.gameObject == null) toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove) noteMarkers.Remove(k);
    }

    private static bool SafeIsStepInFilledBin(InstrumentTrack track, int stepIndex)
    {
        try
        {
            // New API name
            if (track != null && track.IsStepInFilledBin(stepIndex)) return true;
            return false;
        }
        catch
        {
            // Older builds without IsStepInFilledBin → assume filled so UI doesn’t disappear
            return true;
        }
    }

    private void DestroyOrphanRowMarkers(InstrumentTrack track, int activeBurstId)
{
    int trackIndex = Array.IndexOf(_ctrl.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

    var row = trackRows[trackIndex];

    // Build dict-owned set for this track once (prevents O(n^2) checks)
    var owned = new HashSet<Transform>();
    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 != track) continue;
        if (kv.Value) owned.Add(kv.Value);
    }

    var toDestroy = new List<GameObject>();

    for (int i = 0; i < row.childCount; i++)
    {
        var child = row.GetChild(i);
        if (!child) continue;

        var tag = child.GetComponent<MarkerTag>();

        // If untagged and not dict-owned, it's unmanaged "mystery" content.
        if (tag == null)
        {
            bool isOwned = owned.Contains(child);
            if (!isOwned)
            {
                // Treat as orphan candidate (safe because we only act within the row)
                toDestroy.Add(child.gameObject);
            }
            continue;
        }

        // Never treat an in-flight ascent marker as an orphan.
        if (tag.isAscending)
            continue;

        var key = (track, tag.step);

        bool hasKey = noteMarkers.TryGetValue(key, out var tr) && tr;
        bool sameObject = hasKey && tr.gameObject == child.gameObject;

        bool inFilledBin = SafeIsStepInFilledBin(track, tag.step);

        // Only consider destroying placeholders if their bin is filled.
        bool stalePlaceholder = tag.isPlaceholder && inFilledBin && (tag.burstId >= 0) && (tag.burstId != activeBurstId);

        // Duplicate object (dict has key, but points to a different GO)
        bool duplicateForKey = hasKey && !sameObject;

        // Extra safety: if the dict-owned marker is ascending, do not destroy anything for this key.
        if (duplicateForKey)
        {
            var dictTag = tr.GetComponent<MarkerTag>();
            if (dictTag != null && dictTag.isAscending)
                duplicateForKey = false;
        }

        if (stalePlaceholder || duplicateForKey)
            toDestroy.Add(child.gameObject);

    }

    foreach (var go in toDestroy)
        SafeDestroy(go);
}
}
