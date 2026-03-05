using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Chord Progression Profile")]
public class ChordProgressionProfile : ScriptableObject
{
    public string progressionName = "I–IV–V";
    public float beatsPerChord = 4f;
    public List<Chord> chordSequence = new();
}

[System.Serializable]
public struct Chord
{
    public string label; // e.g., "I", "IV", "V" (for debug/display)
    public int rootNote; // MIDI base note, e.g., C = 60
    public List<int> intervals; // e.g., major triad = [0, 4, 7]
}