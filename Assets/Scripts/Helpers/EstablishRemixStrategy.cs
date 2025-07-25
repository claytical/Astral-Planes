using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(menuName = "Astral Planes/Establish Remix Strategy")]
public class EstablishRemixStrategy : TrackUtilityStrategy
{
    public override void Apply(InstrumentTrack track, NoteSet noteSet, MusicalPhaseProfile phase)
    {
        if (noteSet == null || track == null) return;

        // Step 1: Generate ghost phrase for visual/musical cue
        noteSet.GenerateGhostNoteSequence();

        // Step 2: Optional early loop (silent or triggered after collection)
        // For now, don't replace the player's loop — let the ghost inspire improvisation
        Debug.Log($"🎵 Establish phase ghost phrase for {track.name}: {string.Join(", ", noteSet.ghostNoteSequence)}");
    }

    public virtual List<int> RemixFromCollectedNotes(List<int> collectedNotes, NoteSet noteSet)
    {
        if (collectedNotes == null || collectedNotes.Count == 0 || noteSet?.remixUtility == null)
            return new List<int>();

        // Preserve player order, just trim or duplicate for rhythm
        var loop = new List<int>(collectedNotes);

        // Make sure the loop is musically coherent
        if (loop.Count == 1)
            loop.Add(loop[0]);
        else if (loop.Count == 2)
            loop.Add(loop[0]);

        return loop;
    }
}