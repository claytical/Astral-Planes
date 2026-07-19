using UnityEngine;

[CreateAssetMenu(fileName = "InstrumentTrackControllerConfig", menuName = "Astral Planes/Instrument Track Controller Config")]
public class InstrumentTrackControllerConfig : ScriptableObject
{
    [Header("SFX: Collection")]
    [Range(0f, 2f)] public float pickupTickVolume = 1.15f;
    [Range(0f, 0.15f)] public float pickupTickPitchJitter = 0.03f;
    [Tooltip("Fallback clip if role-specific clips are not set.")]
    public AudioClip pickupTickDefault;
    public AudioClip pickupTickBass;
    public AudioClip pickupTickLead;
    public AudioClip pickupTickHarmony;
    public AudioClip pickupTickGroove;
    
    [Header("Gravity Void")]
    public float gravityVoidScale = 1f;
    [Min(1)] public int voidRingWidthCells = 1;
    public float gravityVoidImprintTickSeconds = 0.05f;

    [Header("Cohort")]
    [Tooltip("Fraction of the leader loop used as the cohort arm window.")]
    public float cohortWindowFraction = 0.5f;
}
