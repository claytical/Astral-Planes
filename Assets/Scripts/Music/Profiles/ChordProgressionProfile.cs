using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Music/Motif/Chord Progression Profile", fileName = "ChordProgressionProfile")]
public class ChordProgressionProfile : ScriptableObject
{
    public string progressionName = "I–IV–V";
    public List<Chord> chordSequence = new();
}

[System.Serializable]
public struct Chord
{
    public string label; // e.g., "I", "IV", "V" (for debug/display)
    public int rootNote; // MIDI base note, e.g., C = 60
    public List<int> intervals; // e.g., major triad = [0, 4, 7]
}