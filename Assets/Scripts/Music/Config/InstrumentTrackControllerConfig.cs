using UnityEngine;

[CreateAssetMenu(fileName = "InstrumentTrackControllerConfig", menuName = "Astral Planes/Music/Tracks & Mix/Instrument Track Controller Config")]
public class InstrumentTrackControllerConfig : ScriptableObject
{
    [Header("SFX: Collection")]
    [Range(0f, 2f)] public float pickupTickVolume = 1.15f;
    [Range(0f, 0.15f)] public float pickupTickPitchJitter = 0.03f;
    [Tooltip("Fallback clip if role-specific clips are not set.")]
    public AudioClip pickupTickDefault;
    [Tooltip("Pickup tick for Bass notes (falls back to Pickup Tick Default).")]
    public AudioClip pickupTickBass;
    [Tooltip("Pickup tick for Lead notes (falls back to Pickup Tick Default).")]
    public AudioClip pickupTickLead;
    [Tooltip("Pickup tick for Harmony notes (falls back to Pickup Tick Default).")]
    public AudioClip pickupTickHarmony;
    [Tooltip("Pickup tick for Groove notes (falls back to Pickup Tick Default).")]
    public AudioClip pickupTickGroove;
    
    [Header("Gravity Void")]
    [Tooltip("Visual scale multiplier on the gravity void prefab when it spawns. Detection radius is VehicleConfig.voidProbeRadiusWorld; dust grow-in timing is on CosmicDustGeneratorConfig.")]
    public float gravityVoidScale = 1f;
    [Min(1), Tooltip("Width (grid cells) of each concentric ring the gravity void imprints into the dust as it expands.")] public int voidRingWidthCells = 1;
    [Tooltip("Seconds between gravity void imprint ticks (how fast the void's rings spread through the dust).")]
    public float gravityVoidImprintTickSeconds = 0.05f;

    [Header("Cohort")]
    [Tooltip("Fraction of the leader loop used as the cohort arm window.")]
    public float cohortWindowFraction = 0.5f;
}
