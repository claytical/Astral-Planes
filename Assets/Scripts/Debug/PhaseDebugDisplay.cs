using UnityEngine;
using System.Text;
using UnityEngine.InputSystem;

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
        var currentSet = progressionManager.GetCurrentSpawnerSet();

        StringBuilder label = new StringBuilder();
        label.AppendLine($"<b>🎼 Current Phase:</b> {group.phase}");

        if (currentSet != null)
        {
            label.AppendLine($"<b>🎛 Set:</b> {currentSet.name}");

            foreach (var nodePrefab in currentSet.mineNodes)
            {
                if (nodePrefab == null) continue;

                label.AppendLine($"• <i>{nodePrefab.prefab.name}</i>");

                var mineNode = nodePrefab.prefab.GetComponent<MineNode>();
                if (mineNode != null && mineNode.minedPrefabs != null)
                {
                    foreach (var mined in mineNode.minedPrefabs)
                    {
                        label.AppendLine($"    → {mined?.name ?? "null"}");
                    }
                }
            }
        }

        GUI.Label(new Rect(20, 20, 500, 1000), label.ToString(), labelStyle);
    }
    public void OnNavigate(InputValue value)
    {
        Vector2 direction = value.Get<Vector2>();
        Debug.Log($"🧭 Navigate: {direction}");
    }

}
