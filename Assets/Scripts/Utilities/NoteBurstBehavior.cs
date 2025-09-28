using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class NoteBurstSpawner
{
    /// Spawns a "burst" of Collectables for a single InstrumentTrack using that track's current NoteSet.
    /// Records spawned notes via RegisterSpawnedNotesThisPhase so miss tracking works.
    public static void SpawnBurst(InstrumentTrack track, NoteSet noteSet, Transform parent, Vector3 driftOrigin, float loopSeconds)
    {
        if (track == null || noteSet == null || track.collectablePrefab == null || track.drumTrack == null)
        {
            Debug.LogWarning("NoteBurstSpawner: missing references.");
            return;
        }

        var noteList = noteSet.GetSortedNoteList();
        var stepList = noteSet.GetStepList();
        var nv       = track.controller.noteVisualizer;

        // Precompute ribbon positions for each step (full-width X)
        var targetPositions = new List<Vector3>(stepList.Count);
        foreach (int step in stepList)
            targetPositions.Add(nv.ComputeRibbonWorldPosition(track, step));

        int gridWidth  = track.drumTrack.GetSpawnGridWidth();
        int gridHeight = track.drumTrack.GetSpawnGridHeight();

        var spawnedThisPhase = new List<(int step, int note, int duration, float velocity)>();
        bool spawnedAny = false;
        var usedColumns = new HashSet<int>();

        for (int i = 0; i < stepList.Count; i++)
        {
            int step = stepList[i];
            int note = noteSet.GetNoteForPhaseAndRole(track, step);
            int pitchIndex = noteList.IndexOf(note);
            if (pitchIndex < 0 || pitchIndex >= gridHeight) continue;

            bool didSpawn = false;

            for (int col = 0; col < gridWidth && !didSpawn; col++)
            {
                if (usedColumns.Contains(col)) continue;

                var gridPos = new Vector2Int(col, pitchIndex);
                var cell    = track.drumTrack.spawnGrid.gridCells[gridPos.x, gridPos.y];
                if (cell.isOccupied && cell.objectType == GridObjectType.Note) continue;

                // Blend: ribbon X + lane Y/Z
                Vector3 gridTarget = track.drumTrack.GridToWorldPosition(gridPos);
                Vector3 ribbonPos  = targetPositions[i];
                Vector3 spawnPos   = new Vector3(ribbonPos.x, gridTarget.y, gridTarget.z);

                var go = Object.Instantiate(track.collectablePrefab, driftOrigin, Quaternion.identity, parent);
                if (go.TryGetComponent(out Collectable c))
                {
                    int dur = track.CalculateNoteDurationFromSteps(step, noteSet);
                    spawnedThisPhase.Add((step, note, dur, 1f));

                    c.energySprite.color = track.trackColor;
                    c.Initialize(note, dur, track, noteSet, stepList);
                    c.OnCollected += (d, force) => track.OnCollectableCollected(c, step, d, force);
                    c.OnDestroyed += ()        => track.SendMessage("OnCollectableDestroyed", c);

                    track.drumTrack.OccupySpawnGridCell(gridPos.x, gridPos.y, GridObjectType.Note);
                    track.spawnedCollectables.Add(go);
                    track.PlayNote(note, dur, 100f);

                    float timeLeftOnDrumLoop = track.drumTrack.GetLoopLengthInSeconds() -
                        (track.drumTrack.drumAudioSource.time % track.drumTrack.GetLoopLengthInSeconds());

                    c.DriftToTarget(driftOrigin, spawnPos, timeLeftOnDrumLoop);
                    usedColumns.Add(col);
                    didSpawn   = true;
                    spawnedAny = true;
                }
            }
        }

        // Register for miss tracking
        track.RegisterSpawnedNotesThisPhase(spawnedThisPhase);

        if (!spawnedAny)
            Debug.LogWarning($"NoteBurstSpawner: no valid spawn positions for {track.name}.");
    }
}
