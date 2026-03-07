using UnityEngine;

/// <summary>
/// Place this on the same GameObject as MotifCoralVisualizer in the GeneratedTrack scene.
/// Mirrors the pattern of CoralRigRegistrar — registers the visualizer with GameFlowManager
/// at Awake so GFM can activate it during motif-level bridge moments.
///
/// SETUP
/// ─────
///   GeneratedTrack scene
///   └── MotifCoralVisualizer  (GameObject, inactive by default)
///         ├── MotifCoralVisualizer component  (materials assigned here)
///         └── MotifCoralRegistrar component   (no fields required)
///
/// EXECUTION ORDER
/// ───────────────
///   GameFlowManager must be awake before this. Set Script Execution Order in
///   Project Settings so GameFlowManager runs before Default Time, or rely on
///   GFM being a persistent singleton initialised in an earlier scene.
/// </summary>
public sealed class MotifCoralRegistrar : MonoBehaviour
{
    private void Awake()
    {
        var vis = GetComponent<MotifCoralVisualizer>();

        if (vis == null)
        {
            Debug.LogError("[MotifCoralRegistrar] No MotifCoralVisualizer found on this GameObject.");
            return;
        }

        if (GameFlowManager.Instance == null)
        {
            Debug.LogWarning("[MotifCoralRegistrar] GameFlowManager not ready yet — visualizer will not be registered.");
            return;
        }

        GameFlowManager.Instance.RegisterMotifCoralVisualizer(vis);
    }
}
