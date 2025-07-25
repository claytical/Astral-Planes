using UnityEngine;
using System.Linq;

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
}