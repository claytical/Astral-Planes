using UnityEngine;

[CreateAssetMenu(fileName = "DrumTrackConfig", menuName = "Astral Planes/Music/Tracks & Mix/Drum Track Config")]
public class DrumTrackConfig : ScriptableObject
{
    [Header("Grid Sizing")]
    [Tooltip("Reference screen width (px) for grid auto-sizing; with Reference Columns this fixes the cell size in pixels.")]
    public int referenceWidthPx = 1920;
    [Tooltip("Number of grid columns at the reference width. Cell px = Reference Width Px / Reference Columns.")]
    public int referenceColumns = 36;
    [Tooltip("Pixels reserved at the bottom of the screen (note visualizer strip) that the play area must not cover.")]
    public int uiBottomPaddingPx = 160;
    [Tooltip("Pixels reserved at the top of the screen that the play area must not cover.")]
    public int uiTopPaddingPx = 52;
    [Tooltip("Extra world-unit inset applied to each edge of the play area.")]
    public float gridPadding = 0f;
    [Tooltip("Compute grid columns/rows from the screen size using the reference values above.")]
    public bool autoSizeSpawnGridToScreen = true;
    [Tooltip("Freeze the play area once computed so a resolution/camera change mid-session doesn't shift the grid.")]
    public bool lockPlayAreaAfterInit = true;

    [Header("Beat Timing")]
    [Tooltip("Width (in steps) of the on-beat timing window used when judging collectable pickup timing.")]
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
    [Min(0f), Tooltip("Continuous poll mode: seconds between intensity re-evaluations. The swap itself still lands on the next leader-loop boundary.")]
    public float intensityPollIntervalSeconds = 0.5f;
    [Range(0.01f, 1f), Tooltip("Exponential smoothing factor applied to the carve-driven intensity per evaluation. Higher = drums follow carving faster. The carve pool's half-life already smooths, so this can stay high.")]
    public float intensitySmoothFactor = 0.6f;

    [Header("Bass Drop")]
    [Tooltip("When a Bass note is placed, jump straight to the motif's highest-pressure intensity clip on the next bar.")]
    public bool bassDropEnabled = true;
    [Min(1), Tooltip("Loop boundaries the drop clip holds once audible before carve-driven selection resumes.")]
    public int bassDropHoldLoops = 2;
    [Tooltip("Visual tell: when the drop clip becomes audible, a Bass-colored ring flash sweeps out through the dust from the play-area center.")]
    public bool bassDropShockwaveEnabled = true;
    [Min(1f), Tooltip("Shockwave expansion speed in grid cells per second.")]
    public float bassDropShockwaveCellsPerSecond = 45f;
    [Min(0.05f), Tooltip("How long each dust cell flashes as the wave passes.")]
    public float bassDropShockwaveFlashSeconds = 0.35f;
    [Range(0f, 1f), Tooltip("How far the flash color is pushed from the Bass color toward white.")]
    public float bassDropShockwaveWhiten01 = 0.3f;

    [Header("Axis Smoothing")]
    [Tooltip("Exponential smoothing factor applied to depth01/breadth01 per evaluation (0-1, higher = faster response). Intensity uses intensitySmoothFactor.")]
    public float axisSmoothFactor = 0.35f;
    [Tooltip("Minimum change in a smoothed axis value required before it's allowed to move (dead-zone, prevents flicker).")]
    public float axisHysteresis = 0.08f;
    [Tooltip("Minimum seconds that must elapse since the last actual clip swap before a different clip can be selected, even if it's now the nearest match. Prevents rapid alternation between two nearby clips.")]
    public float minClipDwellSeconds = 4f;

    [Header("Clip Poll Mode")]
    [Tooltip("When drum intensity is re-evaluated: LoopBoundary = once per loop, BinFill = when a track fills a bin, Continuous = every Intensity Poll Interval Seconds.")]
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
