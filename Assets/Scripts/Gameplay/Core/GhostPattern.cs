using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using Steamworks;
using UnityEngine;
public enum GhostPatternStrategy
{
    Arpeggiated,
    StaticRoot,
    WalkingBass,
    MelodicPhrase,
    PercussiveLoop,
    Drone,
    Randomized
}

public class GhostPattern : MonoBehaviour
{
    public MusicalRoleProfile roleProfile;
    public SpriteRenderer[] sprites;
    public ParticleSystem particles;
    private InstrumentTrack track;
    private NoteSet noteSet;
    private int notesPerLoop;
    public void Initialize(Vehicle vehicle, NoteSet noteSet, InstrumentTrack track, MusicalRoleProfile role)
    {
        foreach (var t in sprites)
        {
            Color color = track.trackColor;
            color.a = .1f;
            t.color = color;
        }
// CLEAR TRACK, ENERGY ON RIBBON GOES TO VEHICLE 
        track.ClearLoopedNotes(TrackClearType.EnergyRestore, vehicle);
        track.OnGhostCycleStarted();
        ParticleSystem.MainModule main = particles.main;
        main.startColor = track.trackColor;
        this.noteSet = noteSet;
        this.track = track;
        this.roleProfile = role;
    }

public void Create()
{
    GameFlowManager.Instance.SetGhostCycleState(true);
    // ✅ Use stable drift origin
    Vector3 driftOrigin = transform.parent != null 
        ? transform.parent.position 
        : transform.position;
    List<int> noteList = noteSet.GetSortedNoteList();
    List<int> stepList = noteSet.GetStepList();
    var nv = track.controller.noteVisualizer;

    List<Vector3> targetPositions = new List<Vector3>(stepList.Count);
    foreach (int step in stepList)
        targetPositions.Add(nv.ComputeRibbonWorldPosition(track, step));

    float loopSeconds   = track.drumTrack.GetLoopLengthInSeconds();
    int   gridWidth     = track.drumTrack.GetSpawnGridWidth();
    int   gridHeight    = noteList.Count;
    float spawnChance   = GetSpawnChanceForPhase();
    List<Vector3> path  = new List<Vector3>();
    bool spawnedAny     = false;
    HashSet<int> usedColumns = new(); // 💡 track globally used columns
    List<(int, int, int, float)> spawnedThisPhase = new();
    for (int i = 0; i < stepList.Count; i++)
    {
        int step       = stepList[i];
        int note       = noteSet.GetNoteForPhaseAndRole(track, step);
        int pitchIndex = noteList.IndexOf(note);
        if (pitchIndex < 0 || pitchIndex >= gridHeight) continue;

        bool didSpawn = false;
        

        for (int col = 0; col < gridWidth && !didSpawn; col++)
        {
            if (usedColumns.Contains(col)) continue; // 🚫 skip if column already used
            if (Random.value > spawnChance && spawnedAny) continue;

            Vector2Int gridPos = new Vector2Int(col, pitchIndex);
            if (!track.drumTrack.IsSpawnCellAvailable(gridPos.x, gridPos.y)) continue;

            Vector3 spawnPos   = track.drumTrack.GridToWorldPosition(gridPos);
            Vector3 driftStart = driftOrigin;

            GameObject spawned = Instantiate(
                track.collectablePrefab,
                driftStart,
                Quaternion.identity,
                track.collectableParent
            );

            if (spawned.TryGetComponent(out Collectable c))
            {
                int dur = track.CalculateNoteDurationFromSteps(step, noteSet);
                spawnedThisPhase.Add((step, note, dur, 1f)); // Velocity could be varied

                c.energySprite.color = track.trackColor;
                c.Initialize(note, dur, track, noteSet, stepList);
                c.OnCollected += (d, force) => track.OnCollectableCollected(c, step, d, force);
                c.OnDestroyed += ()        => track.SendMessage("OnCollectableDestroyed", c);

                track.drumTrack.OccupySpawnGridCell(gridPos.x, gridPos.y, GridObjectType.Note);
                track.spawnedCollectables.Add(spawned);
                track.PlayNote(note, dur, 20f);
                
                float timeLeftOnDrumLoop = track.drumTrack.GetLoopLengthInSeconds() - (track.drumTrack.drumAudioSource.time % track.drumTrack.GetLoopLengthInSeconds());

                c.DriftToTarget(driftStart, spawnPos, timeLeftOnDrumLoop); // ✅ animate properly
                path.Add(spawnPos);

                usedColumns.Add(col); // ✅ lock column
                didSpawn   = true;
                spawnedAny = true;
            }
        }
    }
    track.RegisterSpawnedNotesThisPhase(spawnedThisPhase);
    Debug.Log($"📀 Spawned {spawnedThisPhase.Count} notes this phase for {track.name}");

    if (!spawnedAny)
    {
        Debug.LogWarning("❌ No valid spawn locations found — forcing a fallback note.");
        ForceFallbackSpawn(noteList, stepList, nv, targetPositions, loopSeconds, path);
    }

    track.OnGhostCycleEnded();
    Destroy(gameObject);
}

private void ForceFallbackSpawn(List<int> noteList, List<int> stepList, NoteVisualizer nv, List<Vector3> targetPositions, float loopSeconds, List<Vector3> path)
{
    int gridWidth  = track.drumTrack.GetSpawnGridWidth();
    int gridHeight = noteList.Count;

    foreach (int step in stepList)
    {
        int note = noteSet.GetNoteForPhaseAndRole(track, step);
        int pitchIndex = noteList.IndexOf(note);
        if (pitchIndex < 0 || pitchIndex >= gridHeight) continue;

        for (int col = 0; col < gridWidth; col++)
        {
            Vector2Int gridPos = new Vector2Int(col, pitchIndex);
            if (!track.drumTrack.IsSpawnCellAvailable(gridPos.x, gridPos.y)) continue;

            Vector3 spawnPos   = track.drumTrack.GridToWorldPosition(gridPos);
            Vector3 driftStart = transform.position;
            GameObject spawned = Instantiate(
                track.collectablePrefab,
                driftStart,
                Quaternion.identity,
                track.collectableParent
            );

            if (spawned.TryGetComponent(out Collectable c))
            {
                int dur = track.CalculateNoteDurationFromSteps(step, noteSet);
                c.energySprite.color = track.trackColor;
                c.Initialize(note, dur, track, noteSet, stepList);
                c.OnCollected += (d, force) => track.OnCollectableCollected(c, step, d, force);
                c.OnDestroyed += ()        => track.SendMessage("OnCollectableDestroyed", c);
                c.DriftToTarget(driftStart, spawnPos, 1);
                track.drumTrack.OccupySpawnGridCell(gridPos.x, gridPos.y, GridObjectType.Note);
                track.spawnedCollectables.Add(spawned);
                track.PlayNote(note, dur, 20f); 
                path.Add(spawnPos);
                return;
            }
        }
    }
}


private float GetSpawnChanceForPhase()
{
    switch (track.drumTrack.currentPhase)
    {
        case MusicalPhase.Intensify: return 0.9f;
        case MusicalPhase.Evolve: return 0.6f;
        case MusicalPhase.Release: return 0.3f;
        case MusicalPhase.Wildcard: return Random.Range(0.2f, 0.8f);
        default: return 0.5f;
    }
}

}
