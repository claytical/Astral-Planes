using System.Linq;
using TMPro;
using UnityEngine;
using Debug = UnityEngine.Debug;

public partial class GameFlowManager
{
    public void JoinGame()
    {
        var intro = FindByNameIncludingInactive("IntroScreen");
        var setup = FindByNameIncludingInactive("GameSetupScreen");

        if (intro != null && intro.activeSelf)
        {
            intro.SetActive(false);
            if (setup != null)
            {
                string title = FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.InstanceID).FirstOrDefault().text = "Select your vessel...";
                Debug.Log($"Set title to {title}");
                setup.SetActive(true);
            }
        }
    }

    private void SetBridgeCinematicMode(bool on)
    {
        // Existing behavior: hide maze + note UI
        SetBridgeVisualMode(on);

        if (on)
            HideNonCoralRenderersForBridge();
        else
            RestoreNonCoralRenderersAfterBridge();
    }

    private void HideNonCoralRenderersForBridge()
    {
        _bridgeHiddenRenderers.Clear();

        // Hide Vehicles + any other visible gameplay renderers.
        // We intentionally do NOT touch the coral instance here.
        foreach (var r in FindObjectsOfType<Renderer>(includeInactive: true))
        {
            if (!r) continue;

            // Skip the motif coral visualizer so it stays visible during the motif bridge
            if (motifCoralVisualizer != null && r.transform.IsChildOf(motifCoralVisualizer.transform))
                continue;

            // Skip the glyph applicator so its LineRenderers aren't hidden
            if (motifGlyphApplicator != null && r.transform.IsChildOf(motifGlyphApplicator.transform))
                continue;

            // Skip UI canvases (they’re already handled by SetBridgeVisualMode)
            if (r.GetComponentInParent<Canvas>(true) != null)
                continue;

            // Only hide things that are currently visible
            if (r.enabled)
            {
                _bridgeHiddenRenderers.Add(r);
                r.enabled = false;
            }
        }
    }

    private void RestoreNonCoralRenderersAfterBridge()
    {
        for (int i = 0; i < _bridgeHiddenRenderers.Count; i++)
        {
            var r = _bridgeHiddenRenderers[i];
            if (r) r.enabled = true;
        }

        _bridgeHiddenRenderers.Clear();
    }
}
