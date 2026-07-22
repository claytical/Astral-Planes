using System;
using System.Linq;
using UnityEngine;

public partial class NoteVisualizer
{
    // Creates rows for any tracks beyond the inspector-configured set.
    // Redistributes the total Y anchor range evenly so all rows fit without overflowing.
    private void EnsureTrackRowsForAllTracks()
    {
        if (trackRows == null || trackRows.Count == 0 || _ctrl?.tracks == null) return;

        int needed = 0;
        for (int i = 0; i < _ctrl.tracks.Length; i++)
            if (_ctrl.tracks[i] != null) needed = i + 1;

        int existing = trackRows.Count;
        if (existing >= needed) return;

        // Measure total Y anchor range that existing rows collectively cover.
        float totalMin = float.MaxValue, totalMax = float.MinValue;
        foreach (var r in trackRows)
        {
            if (r == null) continue;
            if (r.anchorMin.y < totalMin) totalMin = r.anchorMin.y;
            if (r.anchorMax.y > totalMax) totalMax = r.anchorMax.y;
        }
        if (totalMin >= totalMax) { totalMin = 0f; totalMax = 1f; }

        // Find a non-null template to clone offsetMin/Max.y from.
        RectTransform template = null;
        for (int i = trackRows.Count - 1; i >= 0; i--)
            if (trackRows[i] != null) { template = trackRows[i]; break; }
        if (template == null || template.parent == null) return;

        // Create missing row GameObjects.
        for (int i = existing; i < needed; i++)
        {
            var go = new GameObject($"TrackRow_Auto_{i}", typeof(RectTransform));
            go.transform.SetParent(template.parent, worldPositionStays: false);
            var rt = go.GetComponent<RectTransform>();
            rt.offsetMin = new Vector2(0f, template.offsetMin.y);
            rt.offsetMax = new Vector2(0f, template.offsetMax.y);
            trackRows.Add(rt);
        }

        // Redistribute all rows equally across the total Y range.
        float rowHeight = (totalMax - totalMin) / needed;
        for (int i = 0; i < needed; i++)
        {
            var row = trackRows[i];
            if (row == null) continue;
            float yMin = totalMin + rowHeight * i;
            row.anchorMin = new Vector2(0f, yMin);
            row.anchorMax = new Vector2(1f, yMin + rowHeight);
            row.offsetMin = new Vector2(0f, row.offsetMin.y);
            row.offsetMax = new Vector2(0f, row.offsetMax.y);
        }
    }

    public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex, bool lit = true, int burstId = -1)
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PLACE] Starting for {stepIndex} on {track.name}");
        var key = (track, stepIndex);

        EnsureTrackRowsForAllTracks();
        int trackIndex = Array.IndexOf(_ctrl.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;
        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        bool shouldLight = lit;

        if (TryReuseExistingMarker(key, row, rowRect, track, stepIndex, shouldLight, burstId, out var reused))
            return reused;

        if (TryAdoptMarker(key, row, rowRect, track, stepIndex, shouldLight, lit, burstId, out var adopted))
            return adopted;

        return SpawnNewPersistentMarker(key, row, rowRect, track, stepIndex, shouldLight, burstId);
    }

    private bool TryReuseExistingMarker(
        (InstrumentTrack, int) key, RectTransform row, Rect rowRect,
        InstrumentTrack track, int stepIndex, bool shouldLight, int burstId,
        out GameObject result)
    {
        result = null;
        if (!noteMarkers.TryGetValue(key, out var existing) || !existing || !existing.gameObject.activeInHierarchy)
            return false;

        var existingTag0 = existing.GetComponent<MarkerTag>();
        if (existingTag0 != null && existingTag0.isAscending)
        {
            result = existing.gameObject;
            return true;
        }

        UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, existing);

        if (shouldLight)
        {
            bool inLoop = track.GetPersistentLoopNotes().Any(n => n.Item1 == stepIndex);
            if (inLoop)
            {
                var existingTag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
                existingTag.isPlaceholder = false;
                if (burstId >= 0) existingTag.burstId = burstId;
            }
        }
        else
        {
            var tag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;

            var ml = existing.GetComponent<MarkerLight>() ?? existing.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.DisplayColor);
        }

        result = existing.gameObject;
        return true;
    }

    private bool TryAdoptMarker(
        (InstrumentTrack, int) key, RectTransform row, Rect rowRect,
        InstrumentTrack track, int stepIndex, bool shouldLight, bool lit, int burstId,
        out GameObject result)
    {
        result = null;
        var adopt = TryAdoptExistingAt(track, stepIndex, row);
        if (!adopt) return false;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[NoteViz] Found note to adopt. This shouldn’t happen.");
        noteMarkers[key] = adopt;
        UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, adopt);

        var tag = adopt.GetComponent<MarkerTag>() ?? adopt.gameObject.AddComponent<MarkerTag>();
        tag.track = track;
        tag.step = stepIndex;

        if (shouldLight)
        {
            tag.isPlaceholder = false;
            if (burstId >= 0) tag.burstId = burstId;
        }
        else
        {
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;

            var ml = adopt.GetComponent<MarkerLight>() ?? adopt.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.DisplayColor);
        }

        result = adopt.gameObject;
        return true;
    }

    private GameObject SpawnNewPersistentMarker(
        (InstrumentTrack, int) key, RectTransform row, Rect rowRect,
        InstrumentTrack track, int stepIndex, bool shouldLight, int burstId)
    {
        int totalSteps = Mathf.Max(1, track.GetTotalSteps());
        int binSize = Mathf.Max(1, track.BinSize());
        int leaderBinsForPlacement = GetLeaderBinsForPlacement(track, totalSteps, binSize);
        float xLocal = ComputeXLocalForTrack(rowRect, track, stepIndex, binSize, leaderBinsForPlacement);
        if (GameFlowManager.VerboseLogging) Debug.Log($"xLocal : {xLocal} for track {track.name} stepIndex {stepIndex} lit={shouldLight}");

        float bottomWorldY = GetBottomWorldY();
        float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

        // Idempotent guard — something may have raced us
        if (noteMarkers.TryGetValue(key, out var appeared) && appeared)
        {
            UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, appeared);
            return appeared.gameObject;
        }

        GameObject marker = Instantiate(notePrefab, row, worldPositionStays: false);
        marker.transform.localPosition = new Vector3(xLocal, bottomLocalY, 0f);

        var newTag = marker.GetComponent<MarkerTag>() ?? marker.AddComponent<MarkerTag>();
        newTag.track = track;
        newTag.step = stepIndex;
        newTag.isPlaceholder = !shouldLight;
        if (burstId >= 0) newTag.burstId = burstId;

        noteMarkers[key] = marker.transform;

        ApplyMarkerVisuals(marker, track, shouldLight);
        return marker;
    }

    private void ApplyMarkerVisuals(GameObject marker, InstrumentTrack track, bool isLit)
    {
        var vnm = marker.GetComponent<VisualNoteMarker>();
        var ml = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        if (isLit)
        {
            if (vnm != null) vnm.Initialize(track.DisplayColor);
            ml.LightUp(track.DisplayColor);
        }
        else
        {
            if (vnm != null) vnm.SetWaitingParticles(track.DisplayColor);
            ml.SetGrey(track.DisplayColor);
        }
    }

    private void UpdateMarkerXPreserveYIfAscending(Rect rowRect, RectTransform row, InstrumentTrack track, int stepIndex, Transform marker)
    {
        if (!marker) return;

        int totalSteps = Mathf.Max(1, track.GetTotalSteps());
        int binSize    = Mathf.Max(1, track.BinSize());
        int leaderBinsForPlacement = GetLeaderBinsForPlacement(track, totalSteps, binSize);
        float xLocal   = ComputeXLocalForTrack(rowRect, track, stepIndex, binSize, leaderBinsForPlacement);

        var tag = marker.GetComponent<MarkerTag>();
        bool ascending = (tag != null && tag.isAscending);

        var lp = marker.localPosition;
        marker.localPosition = ascending
            ? new Vector3(xLocal, lp.y, lp.z)      // preserve Y during ascent
            : new Vector3(xLocal, lp.y, 0f);       // preserve Y generally; row handles baseline
    }

    private int GetLeaderBinsForPlacement(InstrumentTrack track, int totalSteps, int binSize) {
        int leaderBinsBase;
        if (_forcedLeaderSteps >= 1) {
            leaderBinsBase = Mathf.Max(1, Mathf.CeilToInt(_forcedLeaderSteps / (float)binSize));
        }
        else {
            leaderBinsBase = Mathf.Max(1, _ctrl.GetCommittedLeaderBins());
        }
        // Ensure placement width can represent this track's current bins.
        int trackBins = Mathf.Max(1, Mathf.CeilToInt(totalSteps / (float)binSize));
        return Mathf.Max(leaderBinsBase, trackBins);
    }

    float ComputeXLocalForTrack(Rect rowRect, InstrumentTrack track, int stepIndex, int binSize, int leaderBinsForPlacement)
    {
        if (track == null) return rowRect.xMin;

        if (stepIndex < 0) return rowRect.xMin;

        binSize = Mathf.Max(1, binSize);
        leaderBinsForPlacement = Mathf.Max(1, leaderBinsForPlacement);

        int binIndex   = stepIndex / binSize;
        int localInBin = stepIndex % binSize;

        float uMin = (float)binIndex / leaderBinsForPlacement;
        float uMax = (float)(binIndex + 1) / leaderBinsForPlacement;

        float uLocal = (localInBin + 0.5f) / binSize;

        float u = Mathf.Lerp(uMin, uMax, uLocal);
        u = Mathf.Clamp01(u);

        return Mathf.Lerp(rowRect.xMin, rowRect.xMax, u);
    }

    private Transform TryAdoptExistingAt(InstrumentTrack track, int stepIndex, RectTransform row)
    {
        // Look in the row for any marker with the same (track,step)
        var tag = row.GetComponentsInChildren<MarkerTag>(includeInactive: true)
            .FirstOrDefault(t => t && t.track == track && t.step == stepIndex);
        return tag ? tag.transform : null;
    }
}
