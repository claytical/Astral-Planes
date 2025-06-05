using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    public InstrumentTrackController trackController;
    public MusicalPhase previousPhase;
    public MusicalPhase currentPhase;

    public void HandlePhaseTransition(MusicalPhase nextPhase)
    {
        previousPhase = currentPhase;
        currentPhase = nextPhase;

        switch (currentPhase)
        {
            case MusicalPhase.Evolve:
            case MusicalPhase.Wildcard:
                ApplyDriftToTracks(1);
                break;

            case MusicalPhase.Intensify:
            case MusicalPhase.Pop:
                ApplyRemixToAllTracks();
                break;

            case MusicalPhase.Release:
            case MusicalPhase.Establish:
                // Soft transition: no action or subtle visuals
                break;
        }
    }

    private void ApplyDriftToTracks(int count = 1)
    {
        var eligibleTracks = trackController.tracks
            .Where(t => t != null && t.GetNoteDensity() > 0)
            .OrderBy(_ => Random.value)
            .Take(count);

        foreach (var track in eligibleTracks)
        {
            track.PerformSmartNoteModification();
        }
    }

    private void ApplyRemixToAllTracks()
    {
        foreach (var track in trackController.tracks)
        {
            if (track == null || !track.HasNoteSet()) continue;

            var noteSet = track.GetCurrentNoteSet();
            if (noteSet == null) continue;

            track.ClearLoopedNotes();
            noteSet.Initialize(track.GetTotalSteps());

            var steps = noteSet.GetStepList();
            var notes = noteSet.GetNoteList();
            if (steps.Count == 0 || notes.Count == 0) continue;

            for (int i = 0; i < Mathf.Min(steps.Count, 6); i++)
            {
                int step = steps[i];
                int note = noteSet.GetNextArpeggiatedNote(step);
                int duration = track.CalculateNoteDuration(step, noteSet);
                float velocity = Random.Range(60f, 100f);
                track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
            }
        }

        trackController.UpdateVisualizer();
    }
}
