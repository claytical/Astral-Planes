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

        StringBuilder label = new StringBuilder();
        label.AppendLine($"<b>🎼 Current Phase:</b> {group.phase}");
        
        GUI.Label(new Rect(20, 20, 500, 1000), label.ToString(), labelStyle);
    }


}
