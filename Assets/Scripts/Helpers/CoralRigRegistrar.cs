using UnityEngine;

public sealed class CoralRigRegistrar : MonoBehaviour
{
    [SerializeField] private SpiralCoralBaseBuilder baseBuilder;
    [SerializeField] private SpiralCoralTrackGrower trackGrower;
    [SerializeField] private GameObject coralRoot;

    private void Awake()
    {
        if (baseBuilder == null) baseBuilder = GetComponentInChildren<SpiralCoralBaseBuilder>(true);
        if (trackGrower == null) trackGrower = GetComponentInChildren<SpiralCoralTrackGrower>(true);

        // Prefer explicit coralRoot; otherwise fall back to the baseBuilder's top object.
        if (coralRoot == null && baseBuilder != null) coralRoot = baseBuilder.gameObject;

        if (GameFlowManager.Instance == null)
        {
            Debug.LogWarning("[CoralRigRegistrar] GameFlowManager not ready yet.");
            return;
        }

        var rootXform = coralRoot != null ? coralRoot.transform : transform;
        GameFlowManager.Instance.RegisterSpiralCoralRig(rootXform, baseBuilder, trackGrower);
    }

}