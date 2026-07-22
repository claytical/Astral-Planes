using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class NoteVisualizer
{
    public void RecomputeTrackLayout(InstrumentTrack track)
    {
        try
        {
            if (track == null) return;

            int trackIndex = Array.IndexOf(_ctrl.tracks, track);
            if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

            RectTransform row = trackRows[trackIndex];
            Rect rowRect = row.rect;

            int totalSteps = Mathf.Max(1, track.GetTotalSteps());
            int binSize = Mathf.Max(1, track.BinSize());

            int leaderBinsBase;
            if (_forcedLeaderSteps >= 1)
                leaderBinsBase = Mathf.Max(1, Mathf.CeilToInt(_forcedLeaderSteps / (float)binSize));
            else
                leaderBinsBase = Mathf.Max(1, _ctrl.GetCommittedLeaderBins());

            int trackBins = Mathf.Max(1, Mathf.CeilToInt(totalSteps / (float)binSize));
            int leaderBinsForPlacement = Mathf.Max(leaderBinsBase, trackBins);

            float bottomWorldY = GetBottomWorldY();
            float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

            var chosenByStep = ReconcileDuplicateMarkersInRow(row, track);
            RepositionAndPruneMarkers(row, track, rowRect, binSize, leaderBinsForPlacement, bottomLocalY, chosenByStep);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RECOMPUTE] EXCEPTION track={track?.name ?? "NULL"} ex={ex}");
        }
    }

    // Pass 1: scan row children, pick the canonical marker per step using priority rules,
    // then force noteMarkers to point at the canonical transform.
    private Dictionary<int, Transform> ReconcileDuplicateMarkersInRow(RectTransform row, InstrumentTrack track)
    {
        var chosenByStep = new Dictionary<int, Transform>(64);
        var chosenTagByStep = new Dictionary<int, MarkerTag>(64);

        for (int i = 0; i < row.childCount; i++)
        {
            var child = row.GetChild(i);
            if (!child) continue;

            var tag = child.GetComponent<MarkerTag>();
            if (tag == null || tag.track != track) continue;

            int step = tag.step;
            if (step < 0) continue;

            if (!chosenByStep.TryGetValue(step, out var existingTf) || !existingTf)
            {
                chosenByStep[step] = child;
                chosenTagByStep[step] = tag;
                continue;
            }

            var existingTag = chosenTagByStep[step];

            // Priority: ascending > non-placeholder > higher burstId
            bool aAsc = tag.isAscending;
            bool bAsc = existingTag != null && existingTag.isAscending;
            bool aPH = tag.isPlaceholder;
            bool bPH = existingTag != null && existingTag.isPlaceholder;
            int aBid = tag.burstId;
            int bBid = existingTag != null ? existingTag.burstId : -999999;

            bool takeA = false;
            if (aAsc != bAsc) takeA = aAsc;
            else if (aPH != bPH) takeA = !aPH;
            else if (aBid != bBid) takeA = aBid > bBid;

            if (takeA)
            {
                chosenByStep[step] = child;
                chosenTagByStep[step] = tag;
            }
        }

        foreach (var kv in chosenByStep)
        {
            int step = kv.Key;
            var tf = kv.Value;
            if (!tf) continue;

            var dictKey = (track, step);
            noteMarkers[dictKey] = tf;
        }

        return chosenByStep;
    }

    // Passes 2+3: reposition canonical markers, then destroy non-canonical duplicates.
    private void RepositionAndPruneMarkers(
        RectTransform row, InstrumentTrack track, Rect rowRect,
        int binSize, int leaderBinsForPlacement, float bottomLocalY,
        Dictionary<int, Transform> chosenByStep)
    {
        var kvs = noteMarkers.ToArray();
        foreach (var kv in kvs)
        {
            var key = kv.Key;
            var tf = kv.Value;
            if (key.Item1 != track || !tf) continue;

            float xLocal = ComputeXLocalForTrack(rowRect, track, key.Item2, binSize, leaderBinsForPlacement);
            var lp = tf.localPosition;
            float yLocal = IsAscending(tf) ? lp.y : bottomLocalY;
            tf.localPosition = new Vector3(xLocal, yLocal, lp.z);
        }

        for (int i = row.childCount - 1; i >= 0; i--)
        {
            var child = row.GetChild(i);
            if (!child) continue;

            var tag = child.GetComponent<MarkerTag>();
            if (tag == null || tag.track != track) continue;

            int step = tag.step;
            if (step < 0) continue;
            if (!chosenByStep.TryGetValue(step, out var canonical) || !canonical) continue;
            if (child == canonical) continue;

            SafeDestroy(child.gameObject);
        }
    }

    public void RequestLeaderGridChange(int newLeaderSteps) {
        // Apply immediately to prevent left-half folding during growth.
        // NOTE: This method previously ignored its parameter; it now becomes the single
        // source of truth for "snap the grid to this leader width" moments.
        _forcedLeaderSteps = (newLeaderSteps > 0) ? Mathf.Max(1, newLeaderSteps) : -1;

         if (_ctrl?.tracks == null) return;
         foreach (var t in _ctrl.tracks)
             if (t) RecomputeTrackLayout(t);
    }

    private void UpdateNoteMarkerPositions()
    {
        // Snapshot to avoid "collection modified" issues if other code mutates noteMarkers this frame.
        var kvs = noteMarkers.ToArray();

        // Reuse a list if you can; shown inline for clarity.
       deadKeys.Clear();

        foreach (var kvp in kvs)
        {
            var track  = kvp.Key.Item1;
            var step   = kvp.Key.Item2;
            var marker = kvp.Value;

            if (marker == null) { deadKeys.Add(kvp.Key); continue; }

            if (track == null) { deadKeys.Add(kvp.Key); continue; }

            if (_trackStepWorldPositions == null || !_trackStepWorldPositions.TryGetValue(track, out var map) || map == null)
                continue;
            int trackIndex = (_ctrl != null && _ctrl.tracks != null) ? Array.IndexOf(_ctrl.tracks, track) : -1;
            if (trackIndex < 0 || trackRows == null || trackIndex >= trackRows.Count) continue;

            RectTransform row = trackRows[trackIndex];
            if (row == null) continue;

            if (map.TryGetValue(step, out var worldPos))
            {
                var lp = marker.localPosition;
                float newX = row.InverseTransformPoint(worldPos).x;
                marker.localPosition = new Vector3(newX, lp.y, lp.z);
            }
        }

        for (int i = 0; i < deadKeys.Count; i++)
            noteMarkers.Remove(deadKeys[i]);
    }

    private static bool IsAscending(Transform tf)
    {
        if (tf == null) return false;
        var tag = tf.GetComponent<MarkerTag>();
        return tag != null && tag.isAscending;
    }
}
