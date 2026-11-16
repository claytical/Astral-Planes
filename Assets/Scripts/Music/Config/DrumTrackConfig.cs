using UnityEngine;

[CreateAssetMenu(fileName = "DrumTrackConfig", menuName = "Astral Planes/Music/Track/Drum Track Config")]
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

    [Header("Carve-Driven Intensity")]
    [Min(0.1f), Tooltip("Half-life (seconds) of the pooled carve-energy accumulator — how fast the beat drains back to the idle clip after the player stops carving.")]
    public float carveActivityHalfLifeSeconds = 3f;
    [Min(0.1f), Tooltip("Weighted carves (within ~one half-life) needed to reach ~63% energy. Higher = more carving needed to push the drums.")]
    public float carveActivitySaturation = 12f;
    [Min(0f), Tooltip("Default energy each carved Bass cell adds (role profiles can override).")]
    public float bassCarveWeight = 1f;
    [Min(0f), Tooltip("Default energy each carved non-Bass cell (incl. filler) adds (role profiles can override).")]
    public float otherCarveWeight = 0.5f;
    [Range(0f, 1f), Tooltip("Default intensity ceiling while a non-Bass role drives. Lifted toward 1 as the Bass track's bins fill. Bass's ceiling is always 1 unless overridden on its profile.")]
    public float otherRoleCeiling01 = 0.6f;
    [Min(0f), Tooltip("Continuous poll mode: seconds between intensity re-evaluations. The swap itself still lands on the next leader-loop boundary.")]
    public float intensityPollIntervalSeconds = 0.5f;
    [Range(0.01f, 1f), Tooltip("Exponential smoothing factor applied to the carve-driven intensity per evaluation. Higher = drums follow carving faster. The carve pool's half-life already smooths, so this can stay high.")]
    public float intensitySmoothFactor = 0.6f;

    [Header("Bass Drop")]
    [Tooltip("When the last note of a Bass collectable burst commits, jump straight to the motif's highest-pressure intensity clip on the next loop boundary.")]
    public bool bassDropEnabled = true;
    [Min(1), Tooltip("Loop boundaries the drop clip holds once audible before carve-driven selection resumes.")]
    public int bassDropHoldLoops = 2;

    [Header("Axis Smoothing")]
    [Tooltip("Exponential smoothing factor applied to depth01/breadth01 per evaluation (0-1, higher = faster response). Intensity uses intensitySmoothFactor.")]
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

public enum ClipPollMode { LoopBoundary, BinFill, Continuous }
