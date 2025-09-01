using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NoteSetConfig", menuName = "Astral Planes/NoteSet Config")]
public class RolePhaseNoteSetConfig : ScriptableObject
{
    public MusicalRole role;
    public MusicalPhase phase;

    public List<ScaleType> possibleScales;
    public List<ChordPattern> possibleChordPatterns;
    public List<RhythmStyle> possibleRhythmStyles;
    public NoteBehavior noteBehavior;

    public ScaleType GetRandomScale() => possibleScales[Random.Range(0, possibleScales.Count)];
    public ChordPattern GetRandomChordPattern() => possibleChordPatterns[Random.Range(0, possibleChordPatterns.Count)];
    public RhythmStyle GetRandomRhythmStyle() => possibleRhythmStyles[Random.Range(0, possibleRhythmStyles.Count)];
}
