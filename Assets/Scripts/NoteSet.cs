using UnityEngine;
using System.Collections.Generic;

public enum NoteBehavior
{
    Bass,       // Sustained, lower-frequency notes
    Lead,       // Fast-moving, melodic notes
    Harmony,    // Chord-based behavior
    Percussion, // Rhythm-based placement
    Drone       // Continuous background texture
}

   
[System.Serializable]
public class WeightedNote
{
    [Range(0,127)]
    public int noteValue; // 🎵 MIDI note number (0-127)
    public int weight = 1; // 📊 Weight (higher = more frequent)
}
[System.Serializable]
public class WeightedDuration
{
    public int durationTicks; // ⏳ Duration in MIDI ticks
    public int weight = 1; // 📊 Higher weight = more frequent selection
}
public class NoteSet : MonoBehaviour
{
    public NoteBehavior noteBehavior;
    public InstrumentTrack assignedInstrumentTrack; // ✅ Each NoteSet is now tied to an InstrumentTrack
    public List<int> allowedSteps = new List<int>(); // ✅ The valid spawn steps for notes
    public List<int> notes = new List<int>(); // ✅ List of possible note values
    public int dominantNote;
    public float dominantNoteFrequency = .7f;
    public int maxExpansionAllowed = 3; // ✅ Limits how many times the loop expands
    public int allowedDuration = -1;
    public int dropBackIndex = 0;
}
