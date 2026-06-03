using UnityEngine;

[CreateAssetMenu(fileName = "InstrumentTrackControllerConfig", menuName = "Astral Planes/Instrument Track Controller Config")]
public class InstrumentTrackControllerConfig : ScriptableObject
{
    [Header("Note Commit")]
    [Tooltip("Performance: all collectables spawn at once; root-note fallback on mistimed release. Composition: collectables spawn step-by-step; collected note always placed at the release step.")]
    public NoteCommitMode noteCommitMode = NoteCommitMode.Performance;

    [Header("SFX: Collection")]
    [Range(0f, 2f)] public float pickupTickVolume = 1.15f;
    [Range(0f, 0.15f)] public float pickupTickPitchJitter = 0.03f;
    [Tooltip("Fallback clip if role-specific clips are not set.")]
    public AudioClip pickupTickDefault;
    public AudioClip pickupTickBass;
    public AudioClip pickupTickLead;
    public AudioClip pickupTickHarmony;
    public AudioClip pickupTickGroove;

    [Header("SFX: Commit")]
    [Range(0f, 2f)] public float commitStingerVolume = 1.25f;
    [Range(0f, 0.15f)] public float commitStingerPitchJitter = 0.02f;
    [Tooltip("Fallback clip if role-specific commit clips are not set.")]
    public AudioClip commitStingerDefault;
    public AudioClip commitStingerBass;
    public AudioClip commitStingerLead;
    public AudioClip commitStingerHarmony;
    public AudioClip commitStingerGroove;
    public AudioClip commitStingerDrums;

    [Header("Gravity Void")]
    public float gravityVoidScale = 1f;
    [Min(1)] public int voidRingWidthCells = 1;
    public float gravityVoidImprintTickSeconds = 0.05f;

    [Header("Cohort")]
    [Tooltip("Fraction of the leader loop used as the cohort arm window.")]
    public float cohortWindowFraction = 0.5f;
}
