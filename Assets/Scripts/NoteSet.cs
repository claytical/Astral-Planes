using UnityEngine;
using System.Collections.Generic;

/*
[System.Serializable]
public class NoteGroup
{
    public InstrumentTrack assignedInstrumentTrack;
    public List<WeightedNote> notes; // 🎵 Notes with weighted probabilities
    public int allowedDuration = -1; // 🎵 Notes with weighted probabilities
    public int maxExpansionAllowed = 2;
    public List<int> allowedSteps;
}
*/
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
    public InstrumentTrack assignedInstrumentTrack; // ✅ Each NoteSet is now tied to an InstrumentTrack
    public List<int> allowedSteps = new List<int>(); // ✅ The valid spawn steps for notes
    public List<int> notes = new List<int>(); // ✅ List of possible note values
    public int allowedDuration = -1; // ✅ Default duration for notes
    public int maxExpansionAllowed = 3; // ✅ Limits how many times the loop expands
}
