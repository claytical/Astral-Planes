using UnityEngine;

[CreateAssetMenu(fileName = "InstrumentTrackConfig", menuName = "Astral Planes/Instrument Track Config")]
public class InstrumentTrackConfig : ScriptableObject
{
    [Header("Ascension")]
    [Tooltip("Fallback ascend-loop count when RoleMotifNoteSetConfig.ascendLoops is 0.")]
    [Min(1)] public int defaultAscendLoops = 4;
    public int ascensionLoopsPerExtraBin = 2;

    [Header("Harmony")]
    [Tooltip("If enabled, notes are treated as authored relative to chord index 0, then root-shifted by the current chord before quantization.")]
    public bool rootShiftNotesByChord = true;

    [Header("Capacity")]
    [Tooltip("Maximum number of bins this track can expand to.")]
    public int maxLoopMultiplier = 4;
}
