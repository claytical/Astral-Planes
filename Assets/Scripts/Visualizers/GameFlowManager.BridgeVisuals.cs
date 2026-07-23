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
                var title = FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None).FirstOrDefault();
                if (title != null)
                {
                    title.text = "Select your vessel...";
                    if (GameFlowManager.VerboseLogging) Debug.Log($"Set title to {title.text}");
                }
                setup.SetActive(true);
            }
        }
    }




}
