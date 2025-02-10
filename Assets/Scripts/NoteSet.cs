using UnityEngine;
using System.Collections.Generic;


[System.Serializable]
public class NoteGroup
{
    public List<WeightedNote> notes; // 🎵 Notes with weighted probabilities
    public int allowedDuration = -1; // 🎵 Notes with weighted probabilities
    public int lockInThreshold = 5; //Number of notes needed to lock in
    public List<int> allowedSteps;
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
    public List<NoteGroup> noteGroups;
}