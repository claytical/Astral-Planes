using UnityEngine;
using System.Text;

public class PhaseDebugDisplay : MonoBehaviour
{

    private GUIStyle labelStyle;
    private bool showDebug = true;


    void OnGUI()
    {
        if (!showDebug)
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

        StringBuilder label = new StringBuilder();
        label.AppendLine($"<b>🎼 Current Phase:</b> ");
        
        GUI.Label(new Rect(20, 20, 500, 1000), label.ToString(), labelStyle);
    }


}
