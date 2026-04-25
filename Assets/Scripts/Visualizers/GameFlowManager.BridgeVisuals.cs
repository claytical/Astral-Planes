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




}
