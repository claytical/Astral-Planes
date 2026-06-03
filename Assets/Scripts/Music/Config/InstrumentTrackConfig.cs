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

    [Header("Spawn")]
    public int spawnPickMaxTries = 80;
    [Range(0f, 1f)]
    [Tooltip("Fraction of grid width to search per step column band.")]
    public float spawnColumnBandFraction = 0.25f;
}
