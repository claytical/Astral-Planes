using UnityEngine;
using System.Collections.Generic;

public abstract class RemixUtility : ScriptableObject
{
    /// <summary>
    /// Generate a phrase of notes the ghost will play based on the given NoteSet.
    /// This represents the musical sketch offered to the player.
    /// </summary>
    public abstract List<int> GeneratePhrase(NoteSet noteSet);

    /// <summary>
    /// Remix the notes collected by the player into a new loop structure.
    /// This is typically called by a TrackUtilityStrategy or loop-building event.
    /// </summary>
    public abstract List<int> Remix(List<int> collectedNotes, NoteSet context);
}