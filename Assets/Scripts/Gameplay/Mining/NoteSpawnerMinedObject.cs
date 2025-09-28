using System;
using System.Collections;
using Effects;
using Gameplay.Mining;
using UnityEngine;

public class NoteSpawnerMinedObject : MonoBehaviour
{
    public MusicalRoleProfile musicalRole;
    private bool hasBeenTriggered = false;

    public InstrumentTrack assignedTrack;
    public NoteSet selectedNoteSet;
    private bool wasCollected = false;
    private float timeToLive = 888f; // seconds or 1 loop cycle
    
    void Start() {
    }
    public void Initialize(InstrumentTrack track, NoteSet noteSet)
    {
        Debug.Log($"Initializing Spawner Mined Object");
        assignedTrack = track;
        musicalRole = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        selectedNoteSet = noteSet;
        if (track == null || noteSet == null)
        {
            Debug.LogWarning("NoteSpawnerMinedObject initialized with missing track or NoteSet.");
            return;
        }

        if (selectedNoteSet != null)
        {
            Debug.Log($"Selected Note Set on Spawner is {selectedNoteSet} for {track} with {musicalRole}");
            selectedNoteSet.assignedInstrumentTrack = track;
            selectedNoteSet.Initialize(track, track.drumTrack.totalSteps);
        }


        ApplyTrackVisuals(track.trackColor);

        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.NoteSpawner));
        }
    }

    private IEnumerator AutoFadeIfUncollected() {
        yield return new WaitForSeconds(timeToLive);
        if (!wasCollected) {
            // Fade out ghost
            assignedTrack.ClearLoopedNotes(TrackClearType.Remix);
            // Play dissolve particles or shimmer
            Destroy(gameObject); // remove ghost
        }
    }

    void OnCollisionEnter2D(Collision2D coll)
    {
        Debug.Log($"NoteSpawnerMinedObject.OnCollisionEnter2D: {coll.gameObject.name}");
        CollectionSoundManager.Instance?.PlayNoteSpawnerSound(assignedTrack, selectedNoteSet);
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
