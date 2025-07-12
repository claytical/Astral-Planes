using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class NoteSpawnerMinedObject : MonoBehaviour
{
    public MusicalRoleProfile musicalRole;
    public NoteSetSeries noteSetSeries;

    private InstrumentTrack assignedTrack;
    private NoteSet selectedNoteSet;
    private bool hasBeenTriggered = false;

    public void Initialize(InstrumentTrack track, NoteSet noteSet)
    {
        assignedTrack = track;
        this.GetComponent<MinedObject>().assignedTrack = track;

        selectedNoteSet = noteSet;
        if (track == null || noteSet == null)
        {
            Debug.LogWarning("NoteSpawnerMinedObject initialized with missing track or NoteSet.");
            return;
        }

        selectedNoteSet.assignedInstrumentTrack = track;
        selectedNoteSet.Initialize(track.drumTrack.totalSteps);
        ApplyTrackVisuals(track.trackColor);
        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.NoteSpawner));
        }

    }

    private IEnumerator ResetTriggerCooldown()
    {
        yield return new WaitForSeconds(3f); // or whatever
        hasBeenTriggered = false;
    }
    public void OnCollected(Vehicle vehicle)
    {
        if (hasBeenTriggered) return;
        if (vehicle != null)
        {
            hasBeenTriggered = true;
            StartCoroutine(ResetTriggerCooldown());
            if (assignedTrack != null && selectedNoteSet != null)
            {
                StartCoroutine(HandleAstralTransfer(vehicle));
            }
        }

    }
    
    private IEnumerator HandleAstralTransfer(Vehicle vehicle)
    {
        if (assignedTrack == null || selectedNoteSet == null)
        {
            Debug.LogWarning("❌ Astral transfer failed — missing track or note set.");
            yield break;
        }

//        vehicle.LockControl();
//        vehicle.HideVisuals();

        // 💨 1. Play the gas cloud pulse
        PlayEnvelopePulse();

        yield return new WaitForSeconds(1); // Let the effect breathe

        GameObject ghost = Instantiate(vehicle.ghostVehiclePrefab, transform.position, Quaternion.identity);
        GhostConstellation ghostScript = ghost.GetComponent<GhostConstellation>();
        if (ghostScript == null)
        {
            Debug.LogError("❌ Missing GhostConstellation script on prefab.");
            yield break;
        }

        ghostScript.StartDrift(selectedNoteSet, assignedTrack, musicalRole);

        // 🌫 4. Fade this spawner out
        var explode = GetComponent<Explode>();
        if (explode != null)
            explode.Permanent();
        else
            Destroy(gameObject);
    }

    private void SpawnConstellationSparks(List<Vector3> path)
    {
        foreach (var pos in path)
        {
            GameObject spark = Instantiate(musicalRole.sparkParticlePrefab, pos, Quaternion.identity); // You define sparkPrefab
            Destroy(spark, 2f); // Fade after visual
        }
    }
    private List<Vector3> GenerateConstellationPath()
    {
        if (assignedTrack == null || selectedNoteSet == null)
            return null;

        List<Vector3> path = new List<Vector3>();
        List<int> noteList = selectedNoteSet.GetSortedNoteList();
        List<int> stepList = selectedNoteSet.GetStepList();

        float loopDuration = assignedTrack.drumTrack.GetLoopLengthInSeconds();
        int gridWidth = assignedTrack.drumTrack.GetSpawnGridWidth();
        int gridHeight = noteList.Count;

        int column = 0;

        foreach (int step in stepList)
        {
            if (column >= gridWidth)
                break;

            int note = selectedNoteSet.GetNextArpeggiatedNote(step);
            int pitchIndex = noteList.IndexOf(note);
            if (pitchIndex == -1 || pitchIndex >= gridHeight)
            {
                column++;
                continue;
            }

            Vector2Int gridPos = new Vector2Int(column, pitchIndex);
            if (!assignedTrack.drumTrack.IsSpawnCellAvailable(gridPos.x, gridPos.y))
            {
                column++;
                continue;
            }

            Vector3 spawnPos = assignedTrack.drumTrack.GridToWorldPosition(gridPos);
            path.Add(spawnPos);
            column++;
        }

        return path;
    }

    private void PlayEnvelopePulse()
    {
        if (musicalRole == null || musicalRole.envelopeParticlePrefab == null) return;

        GameObject pulse = Instantiate(musicalRole.envelopeParticlePrefab, transform.position, Quaternion.identity);
    
        var ps = pulse.GetComponent<ParticleSystem>();
        if (ps != null)
        {
            var main = ps.main;
            main.startColor = musicalRole.defaultColor;
            ps.Play();
        }

        Destroy(pulse, 2f);
    }

// 🔁 New callback for when path is ready
    private void OnConstellationPathReady(List<Vector3> notePath)
    {
        var vehicle = FindObjectOfType<Vehicle>(); // If you're assigning, use tracked vehicle
        if (vehicle != null)
        {
            vehicle.StartCoroutine(vehicle.EnterConstellationDrift(notePath));
        }
    }


    private void ApplyTrackVisuals(Color color)
    {
        var visual = GetComponent<TrackItemVisual>();
        if (visual != null)
        {
            visual.trackColor = color;
        }
    }
}
