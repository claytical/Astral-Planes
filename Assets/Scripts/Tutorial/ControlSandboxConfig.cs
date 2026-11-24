using UnityEngine;

/// <summary>
/// Tuning for the pre-motif control sandbox (ControlSandboxDirector). Dialogue lives in
/// Resources/Tutorial/ControlSandbox.json; this asset is pacing, panel docking and the dust field.
/// Loaded from Resources/Config/ControlSandboxConfig; code defaults apply if the asset is missing.
/// </summary>
[CreateAssetMenu(menuName = "Astral Planes/Control Sandbox Config", fileName = "ControlSandboxConfig")]
public class ControlSandboxConfig : ScriptableObject
{
    public const string ResourcePath = "Config/ControlSandboxConfig";

    [Header("Control panel: present -> dock")]
    [Tooltip("Seconds the panel holds centred at full size on a new command before shrinking into the corner.")]
    public float presentSeconds = 1.6f;
    [Tooltip("Docked size as a fraction of the full-size panel.")]
    [Range(0.1f, 1f)] public float dockScale = 0.4f;
    [Tooltip("Gap (canvas units) between the docked panel and the screen edge / narration panel.")]
    public float dockMargin = 32f;
    [Tooltip("Smoothing speed for the present <-> dock move (higher = snappier).")]
    public float dockLerpSpeed = 5f;
    [Tooltip("Beat after a command's practice is satisfied (confirm sound) before the next command presents.")]
    public float stepConfirmBeatSeconds = 0.6f;

    [Header("Gates")]
    [Tooltip("Seconds of drifting (stick past the threshold, any player) after the panel docks.")]
    public float driftPracticeSeconds = 2.5f;
    [Tooltip("Seconds of charging (trigger held, any player) before the dust lesson starts.")]
    public float chargePracticeSeconds = 1.5f;
    [Tooltip("Cells of EACH sandbox role that must be carved (any player) to finish. When a colour reaches this, the rest of its patch dissolves.")]
    public int cellsPerRole = 4;
    [Tooltip("Seconds over which a finished colour's remaining patch dissolves.")]
    public float patchDissolveSeconds = 0.8f;
    [Range(0f, 1f)] public float inputThreshold = 0.35f;

    [Header("Dust field")]
    [Tooltip("At most this many roles get a patch (picked soft -> hard from the destination motif's roles).")]
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
