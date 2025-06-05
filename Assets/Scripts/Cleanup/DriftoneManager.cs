using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class DriftoneManager : MonoBehaviour
{
    public bool isDriftoneActive = false;
    private float driftoneTimer = 0f;
    public float maxDriftDuration = 20f;

    private List<InstrumentTrack> activeTracks;
    private Coroutine driftRoutine;


    public void BeginDriftone(List<InstrumentTrack> tracks)
    {
        isDriftoneActive = true;
        driftoneTimer = 0f;
        activeTracks = tracks;

        foreach (var track in tracks)
        {
            track.isDriftoneActive = true;
            track.isDriftoneLocked = false;
            track.wasLockedByPlayer = false;
        }

        if (driftRoutine != null)
            StopCoroutine(driftRoutine);

        driftRoutine = StartCoroutine(DriftoneLoop());
    }

    private IEnumerator DriftoneLoop()
    {
        Debug.Log("🎼 Driftone loop started.");
        while (driftoneTimer < maxDriftDuration && !AllTracksLocked())
        {
            foreach (var track in activeTracks)
            {
                if (!track.isDriftoneLocked)
                {
                    ApplyDriftToTrack(track);
                }
            }

            driftoneTimer += 1f;
            yield return new WaitForSeconds(1f);
        }

        EndDriftone();
    }

    void EndDriftone()
    {
        isDriftoneActive = false;

        foreach (var track in activeTracks)
        {
            track.isDriftoneActive = false;
        }

        if (driftRoutine != null)
        {
            StopCoroutine(driftRoutine);
            driftRoutine = null;
        }

        Debug.Log("🌙 Driftone ended.");
    }

    public void ApplyDriftToTrack(InstrumentTrack track)
    {
        var noteSet = track.GetCurrentNoteSet();
        if (noteSet == null)
        {
            Debug.LogError(($"{track.name} has no not set"));
        }

        // Slowly nudge root toward a central pitch
        int targetRoot = 48; // Example: C3
        int delta = targetRoot - noteSet.rootMidi;
        if (Mathf.Abs(delta) > 1)
            noteSet.ShiftRoot(Math.Sign(delta)); // One semitone per cycle

        // Drift chord pattern toward RootTriad
        if (noteSet.chordPattern != ChordPattern.RootTriad)
        {
            noteSet.AdvanceChord(); // Cycle closer over time
        }

        // Align persistent notes using voice-leading
        var currentLoop = track.GetPersistentLoopNotes();
        var scale = noteSet.GetNoteList();
        for (int i = 0; i < currentLoop.Count; i++)
        {
            var (step, note, dur, vel) = currentLoop[i];
            int alignedNote = noteSet.GetClosestVoiceLeadingNote(note, scale);
            currentLoop[i] = (step, alignedNote, dur, vel);
        }
    }

    void Update()
    {
        if (!isDriftoneActive) return;

        driftoneTimer += Time.deltaTime;

        foreach (var track in activeTracks)
        {
            if (track.isDriftoneLocked) continue;

            ApplyDriftToTrack(track);
        }

        if (driftoneTimer >= maxDriftDuration || AllTracksLocked())
        {
            EndDriftone();
        }
    }
    
    bool AllTracksLocked() => activeTracks.All(t => t.isDriftoneLocked);
}
