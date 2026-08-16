using UnityEngine;

[CreateAssetMenu(fileName = "DrumTrackConfig", menuName = "Astral Planes/Drum Track Config")]
public class DrumTrackConfig : ScriptableObject
{
    [Header("Grid Sizing")]
    public int referenceWidthPx = 1920;
    public int referenceColumns = 36;
    public int uiBottomPaddingPx = 160;
    public int uiTopPaddingPx = 52;
    public float gridPadding = 0f;
    public bool autoSizeSpawnGridToScreen = true;
    public bool lockPlayAreaAfterInit = true;

    [Header("Beat Timing")]
    public float timingWindowSteps = 0.25f;

    [Header("Intensity Tuning")]
    [Tooltip("Minimum change in density01 required before allowing a new target profile.")]
    public float intensityHysteresis = 0.08f;
    [Tooltip("CosmicDust solid fraction (0-1) at/below which the density nudge is 0.")]
    public float minDensityForIntensity = 0.15f;
    [Tooltip("CosmicDust solid fraction (0-1) at/above which the density nudge saturates to 1.")]
    public float maxDensityForIntensity = 0.65f;
    [Tooltip("Total swing that maze density can nudge intensity01 around the track-fullness tier (density01 is centered at 0.5, so the max nudge in either direction is half this value).")]
    public float densityNudgeRange = 0.3f;
}
