using UnityEngine;

[CreateAssetMenu(fileName = "DrumTrackConfig", menuName = "Astral Planes/Drum Track Config")]
public class DrumTrackConfig : ScriptableObject
{
    [Header("Grid Sizing")]
    public int referenceWidthPx = 1920;
    public int referenceColumns = 36;
    public int uiBottomPaddingPx = 160;
    public float gridPadding = 0f;
    public bool autoSizeSpawnGridToScreen = true;
    public bool lockPlayAreaAfterInit = true;

    [Header("Beat Timing")]
    public float timingWindowSteps = 0.25f;

    [Header("Intensity Tuning")]
    [Tooltip("EMA smoothing for the session baseline (aggregate energy burn per loop). Higher adapts faster.")]
    public float sessionBurnEmaAlpha = 0.25f;
    [Tooltip("How many multiples above baseline maps to full intensity (>=1). 2.5 means 2.5x baseline => intensity 1.")]
    public float burnMultipleAtFullIntensity = 2.5f;
    [Tooltip("Intensity ceiling when exactly 1 instrument track has notes in the upcoming bin (0=low clip only, 1=uncapped). 2+ tracks always uncap.")]
    public float singleTrackIntensityCeiling = 0.45f;
    [Tooltip("How many spent tanks per loop counts as full intensity. Tune while testing.")]
    public float tanksPerLoopAtFullIntensity = 0.35f;
    [Tooltip("Minimum change in intensity01 required before allowing a new target profile.")]
    public float intensityHysteresis = 0.08f;
}
