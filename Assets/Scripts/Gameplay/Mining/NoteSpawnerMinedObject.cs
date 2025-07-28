using System;
using System.Collections;
using System.Collections.Generic;
using Effects;
using Gameplay.Mining;
using UnityEngine;

public class NoteSpawnerMinedObject : MonoBehaviour
{
    public MusicalRoleProfile musicalRole;
    public NoteSetSeries noteSetSeries;
    private bool hasBeenTriggered = false;

    public InstrumentTrack assignedTrack;
    public NoteSet selectedNoteSet;
    public GameObject ghostTrigger;
    private bool wasCollected = false;
    private float timeToLive = 8f; // seconds or 1 loop cycle
    
    void Start() {
        if (assignedTrack.drumTrack.collectionMode == NoteCollectionMode.TimedPuzzle)
            StartCoroutine(AutoFadeIfUncollected());
    }
    public void Initialize(InstrumentTrack track, NoteSet noteSet, NoteSetSeries series = null)
    {
        assignedTrack = track;
        musicalRole = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        selectedNoteSet = noteSet;
        noteSetSeries = series;
        if (track == null || noteSet == null)
        {
            Debug.LogWarning("NoteSpawnerMinedObject initialized with missing track or NoteSet.");
            return;
        }

        if (selectedNoteSet != null)
        {
            selectedNoteSet.assignedInstrumentTrack = track;
            selectedNoteSet.Initialize(track, track.drumTrack.totalSteps);
        }

        ApplyTrackVisuals(track.trackColor);

        if (series == null)
        {
            // Attempt dynamic fallback
            noteSetSeries = MusicalPhaseLibrary.GetNoteSetSeries(track.drumTrack.currentPhase, track.assignedRole);
        }
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

    private IEnumerator ResetTriggerCooldown()
    {
        yield return new WaitForSeconds(3f); // or whatever
        hasBeenTriggered = false;
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
