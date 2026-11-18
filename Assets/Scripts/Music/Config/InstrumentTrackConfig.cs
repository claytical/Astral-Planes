using UnityEngine;

[CreateAssetMenu(fileName = "InstrumentTrackConfig", menuName = "Astral Planes/Music/Tracks & Mix/Instrument Track Config")]
public class InstrumentTrackConfig : ScriptableObject
{
    [Header("Ascension")]
    [Tooltip("Fallback ascend-loop count when the motif's RoleMotifNoteSetConfig.ascendLoops is 0. (NoteAscensionDirector.ascendLoops is a separate marker-animation default.)")]
    [Min(1)] public int defaultAscendLoops = 4;
    public int ascensionLoopsPerExtraBin = 2;

    [Header("Harmony")]
    [Tooltip("If enabled, notes are treated as authored relative to chord index 0, then root-shifted by the current chord before quantization.")]
    public bool rootShiftNotesByChord = true;

    [Header("Capacity")]
    [Tooltip("Maximum number of bins this track can expand to.")]
    public int maxLoopMultiplier = 4;
}
