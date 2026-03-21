using UnityEngine;

public sealed class MotifGlyphRegistrar : MonoBehaviour
{
    private void Awake()
    {
        var applicator = GetComponent<GlyphApplicator>();
        if (applicator == null) { Debug.LogError("[MotifGlyphRegistrar] No GlyphApplicator found."); return; }
        if (GameFlowManager.Instance == null) { Debug.LogWarning("[MotifGlyphRegistrar] GFM not ready."); return; }
        GameFlowManager.Instance.RegisterGlyphApplicator(applicator);
    }
}
