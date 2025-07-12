using System.Collections;
using System.Collections.Generic;
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

public class GhostConstellation : MonoBehaviour
{
    public MusicalRoleProfile roleProfile;
    public SpriteRenderer[] sprites;
    public ParticleSystem particles;
    private InstrumentTrack track;
    private NoteSet noteSet;
    private int notesPerLoop;
    public void StartDrift(NoteSet noteSet, InstrumentTrack track, MusicalRoleProfile role)
    {
        foreach (var t in sprites)
        {
            Color color = track.trackColor;
            color.a = .1f;
            t.color = color;
        }
        ParticleSystem.MainModule main = particles.main;
        main.startColor = track.trackColor;
        this.noteSet = noteSet;
        this.track = track;
        this.roleProfile = role;
        StartCoroutine(DriftRoutine(transform.position));
    }

private IEnumerator DriftRoutine(Vector3 startingPosition)
{
    List<Vector3> path = new List<Vector3>();

    List<int> noteList  = noteSet.GetSortedNoteList();
    List<int> stepList  = noteSet.GetStepList();

    float loopSeconds   = track.drumTrack.GetLoopLengthInSeconds();
    int   loopCount     = GetLoopCountForPhase();                     // ⬅ phase-aware
    int   gridWidth     = track.drumTrack.GetSpawnGridWidth();
    int   gridHeight    = noteList.Count;

    // how long to wait between each hop
    float spacing = (loopSeconds * loopCount) / Mathf.Max(1, gridWidth);

    float spawnChance = GetSpawnChanceForPhase();                     // ⬅ phase-aware density
    int   column      = 0;

    foreach (int step in stepList)
    {
        if (column >= gridWidth) break;

        // probabilistically skip steps to create sparser phrases
        if (Random.value > spawnChance)
        {
            column++;
            continue;
        }

        int note       = noteSet.GetNoteForPhaseAndRole(track, step);
        int pitchIndex = noteList.IndexOf(note);
        if (pitchIndex < 0 || pitchIndex >= gridHeight) { column++; continue; }

        Vector2Int gridPos = new Vector2Int(column, pitchIndex);
        if (!track.drumTrack.IsSpawnCellAvailable(gridPos.x, gridPos.y)) { column++; continue; }

        Vector3 spawnPos = track.drumTrack.GridToWorldPosition(gridPos);
        Vector3 from     = path.Count > 0 ? path[^1] : startingPosition;

        // ✨ place a spark as a beacon
        GameObject spark = null;
        if (roleProfile?.sparkParticlePrefab != null)
            spark = Instantiate(roleProfile.sparkParticlePrefab, spawnPos, Quaternion.identity);

        // 👻 glide to next point
        yield return MoveWithSine(from, spawnPos, spacing);

        if (spark) Destroy(spark, 0.1f);
        transform.position = spawnPos;
        path.Add(spawnPos);

        // 🎵 spawn collectable note
        GameObject spawned = Instantiate(track.collectablePrefab, spawnPos, Quaternion.identity, track.collectableParent);
        if (spawned.TryGetComponent(out Collectable c))
        {
            int dur = track.CalculateNoteDurationFromSteps(step, noteSet);
            c.energySprite.color = track.trackColor;
            c.Initialize(note, dur, track, noteSet);

            c.OnCollected += (d, force) => track.OnCollectableCollected(c, step, d, force);
            c.OnDestroyed += ()        => track.SendMessage("OnCollectableDestroyed", c);

            track.drumTrack.OccupySpawnGridCell(gridPos.x, gridPos.y, GridObjectType.Note);
            track.spawnedCollectables.Add(spawned);
            track.PlayNote(note, dur, 20f);
        }

        yield return new WaitForSeconds(spacing);
        column++;
    }

    Destroy(gameObject); // 👻 fade away
}

private int GetLoopCountForPhase()
{
    switch (track.drumTrack.currentPhase)
    {
        case MusicalPhase.Release:
            return 4;
        case MusicalPhase.Establish:
            return 3;
        case MusicalPhase.Evolve:
            return 2;
        case MusicalPhase.Intensify:
            return 1;
        case MusicalPhase.Wildcard:
            return Random.Range(1, 4);
        case MusicalPhase.Pop:
        default:
            return 2;
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

    private IEnumerator MoveWithSine(Vector3 from, Vector3 to, float duration)
    {
        float amplitude = track.drumTrack.GetGridCellSize() * 0.5f;
        Vector3 dir = (to - from).normalized;
        Vector3 perp = new Vector3(-dir.y, dir.x, 0);

        for (float t = 0; t < duration; t += Time.deltaTime)
        {
            float prog = t / duration;
            float sine = Mathf.Sin(prog * Mathf.PI * 2f) * amplitude;
            transform.position = Vector3.Lerp(from, to, prog) + perp * sine;

            yield return null;
        }

        transform.position = to;
    }
}
