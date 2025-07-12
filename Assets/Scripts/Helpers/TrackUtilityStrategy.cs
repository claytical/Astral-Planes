using UnityEngine;
using System.Collections.Generic;

public abstract class TrackUtilityStrategy : ScriptableObject
{
    /// <summary>
    /// Called when a utility is triggered during gameplay.
    /// Strategies may use the NoteSet’s remixUtility to create ghost phrases or apply player-collected note remixes.
    /// </summary>
    /// <param name="track">The instrument track being modified.</param>
    /// <param name="noteSet">The active NoteSet associated with the track.</param>
    /// <param name="phase">The current musical phase.</param>
    public abstract void Apply(InstrumentTrack track, NoteSet noteSet, MusicalPhaseProfile phase);

    /// <summary>
    /// Optional helper to remix collected notes if the strategy wants to respond to player input.
    /// </summary>
    /// <param name="collectedNotes">The notes collected by the player during the phase.</param>
    /// <param name="noteSet">The context NoteSet used for harmony/pitch rules.</param>
    /// <returns>Remixed loop notes.</returns>
    public virtual List<int> RemixFromCollectedNotes(List<int> collectedNotes, NoteSet noteSet)
    {
        if (noteSet?.remixUtility == null || collectedNotes == null || collectedNotes.Count == 0)
            return new List<int>();

        return noteSet.remixUtility.Remix(collectedNotes, noteSet);
    }
}