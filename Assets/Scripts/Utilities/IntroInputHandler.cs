using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
public class IntroInputHandler : MonoBehaviour
{
    public GameObject quoteText;
    public GameObject gameModeSelect;
    private bool inputReceived = false;

    public void OnAnyInput(InputAction.CallbackContext context)
    {
        if (inputReceived || !context.performed) return;
        inputReceived = true;
        if (ControlTutorialDirector.Instance != null)
            ControlTutorialDirector.Instance.HidePrimary();

        // Hide quote
        quoteText.SetActive(false);
        //AUTOSTART THE RIVER
        GameFlowManager.Instance.StartShipSelectionPhase();
        PlaneSelection();
    }
    
    private void PlaneSelection()
    {
        SceneManager.LoadScene("TrackSelection");
    }

}