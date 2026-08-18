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

    [Header("Axis Tuning (depth / breadth / pressure)")]
    [Tooltip("Weight of dust density in drumPressure01. Paired with pressureFuelWeight; both are normalized internally so they need not sum to 1.")]
    public float pressureDustWeight = 0.5f;
    [Tooltip("Weight of fleet fuel scarcity in drumPressure01. Paired with pressureDustWeight.")]
    public float pressureFuelWeight = 0.5f;
    [Tooltip("Exponential smoothing factor applied to each of depth01/breadth01/pressure01 per evaluation (0-1, higher = faster response).")]
    public float axisSmoothFactor = 0.35f;
    [Tooltip("Minimum change in a smoothed axis value required before it's allowed to move (dead-zone, prevents flicker).")]
    public float axisHysteresis = 0.08f;
    [Tooltip("Minimum seconds that must elapse since the last actual clip swap before a different clip can be selected, even if it's now the nearest match. Prevents rapid alternation between two nearby clips.")]
    public float minClipDwellSeconds = 4f;

    [Header("Clip Poll Mode")]
    public ClipPollMode clipPollMode = ClipPollMode.LoopBoundary;
    [Tooltip("Minimum seconds between BinFill-mode re-evaluations, to prevent thrashing when multiple tracks fill bins close together.")]
    public float binFillPollDebounceSeconds = 0.15f;

    [Header("Transitions")]
    [Min(1), Tooltip("Default number of completed leader-loop boundaries a motif/clip change must wait before it's allowed to arm, giving the outgoing content a full musical phrase before swapping. 1 = arms on the very next loop. Overridable per-motif via MotifProfile.phraseLengthLoopsOverride.")]
    public int defaultTransitionPhraseLengthLoops = 1;
    [Min(0f), Tooltip("Crossfade window (seconds) straddling a deck swap: outgoing deck ramps to 0 and incoming deck ramps from 0 to full over this window, ending exactly at the bar-aligned swap instant. 0 = hard cut. Practical minimum ~0.05-0.1s to avoid a stepped ramp at low frame rates.")]
    public float deckCrossfadeSeconds = 0.15f;
}

public enum ClipPollMode { LoopBoundary, BinFill }
