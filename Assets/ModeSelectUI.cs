using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class ModeSelectUI : MonoBehaviour
{
    public void OnSelectRiver()
    {
        GameFlowManager.Instance.SetSelectedMode("The River");
        GameFlowManager.Instance.StartShipSelectionPhase();
    }

    public void OnSelectFire()
    {
        GameFlowManager.Instance.SetSelectedMode("The Fire");
        GameFlowManager.Instance.StartShipSelectionPhase();
    }

}