using UnityEngine;
using System.Text;

public class PhaseDebugDisplay : MonoBehaviour
{
    public MineNodeProgressionManager progressionManager;

    private GUIStyle labelStyle;
    private bool showDebug = true;


    void OnGUI()
    {
        if (!showDebug || progressionManager == null || progressionManager.phaseQueue == null)
            return;

        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.cyan },
                richText = true
            };
        }

        var phaseGroups = progressionManager.phaseQueue.phaseGroups;
        int phaseIndex = progressionManager.GetCurrentPhaseIndex();
        if (phaseIndex < 0 || phaseIndex >= phaseGroups.Count)
            return;

        var group = phaseGroups[phaseIndex];
        var currentSet = progressionManager.GetCurrentSpawnerStrategyProfile();

        StringBuilder label = new StringBuilder();
        label.AppendLine($"<b>🎼 Current Phase:</b> {group.phase}");

        if (currentSet != null)
        {
            label.AppendLine($"<b>🎛 Set:</b> {currentSet.name}");

            foreach (var nodePrefab in currentSet.mineNodes)
            {
                if (nodePrefab == null) continue;

                label.AppendLine($"• <i>{nodePrefab.ToString()}</i>");
                
            }
        }

        GUI.Label(new Rect(20, 20, 500, 1000), label.ToString(), labelStyle);
    }


}
