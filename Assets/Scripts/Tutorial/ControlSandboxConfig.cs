using UnityEngine;

/// <summary>
/// Tuning for the pre-motif control sandbox (ControlSandboxDirector). Dialogue lives in
/// Resources/Tutorial/ControlSandbox.json; this asset is pacing, the control panel and the dust field.
/// Loaded from Resources/Config/ControlSandboxConfig; code defaults apply if the asset is missing.
/// </summary>
[CreateAssetMenu(menuName = "Astral Planes/Control Sandbox Config", fileName = "ControlSandboxConfig")]
public class ControlSandboxConfig : ScriptableObject
{
    public const string ResourcePath = "Config/ControlSandboxConfig";

    [Header("Narration")]
    [Tooltip("Quiet beat after the sandbox starts before the first line fades in.")]
    public float introDelaySeconds = 2.5f;

    [Header("Control panel (one command at a time, centred)")]
    [Tooltip("Seconds each command takes to fade in.")]
    public float panelFadeInSeconds = 0.8f;
    [Tooltip("Seconds a command takes to fade out once its input is detected.")]
    public float panelFadeOutSeconds = 0.5f;
    [Tooltip("Gap after a command has faded out before the next one fades in.")]
    public float stepConfirmBeatSeconds = 0.6f;

    [Header("Gates")]
    [Tooltip("Seconds of drifting (stick past the threshold, any player), counted from when the command appears.")]
    public float driftPracticeSeconds = 2.5f;
    [Tooltip("Seconds the trigger is held (any player, with or without energy), counted from when the command appears, before the dust lesson starts.")]
    public float chargePracticeSeconds = 0.75f;
    [Tooltip("Cells of EACH sandbox role that must be carved (any player) to finish. When a colour reaches this, the rest of its patch dissolves.")]
    public int cellsPerRole = 4;
    [Tooltip("Seconds over which a finished colour's remaining patch dissolves.")]
    public float patchDissolveSeconds = 0.8f;
    [Range(0f, 1f)] public float inputThreshold = 0.35f;

    [Header("Dust field")]
    [Tooltip("Number of dust colours (one patch each). The destination motif's roles come first, padded from other roles for a soft -> hard spread.")]
    [Range(1, 5)] public int maxRoles = 3;
    [Tooltip("Radius of each role's patch, in grid cells.")]
    public int patchRadiusCells = 3;
    [Tooltip("Patch centres sit at this fraction of the grid height (vehicles launch near the bottom).")]
    [Range(0.2f, 0.9f)] public float patchRowFraction = 0.6f;
    [Tooltip("No dust spawns within this many cells of a vehicle.")]
    public int vehicleClearRadiusCells = 3;
    public float dustGrowInSeconds = 0.8f;

    [Header("Skip (returning players)")]
    [Tooltip("Seconds of holding South to skip straight to the path.")]
    public float skipHoldSeconds = 1.5f;

    [Header("Hints")]
    [Tooltip("Seconds of charging during the dust lesson with no carve before the 'hard dust' hint (once).")]
    public float stuckHintSeconds = 4f;

    public static ControlSandboxConfig LoadOrDefault()
    {
        var cfg = Resources.Load<ControlSandboxConfig>(ResourcePath);
        if (cfg != null) return cfg;
        cfg = CreateInstance<ControlSandboxConfig>();
        cfg.hideFlags = HideFlags.DontSave;
        return cfg;
    }
}
