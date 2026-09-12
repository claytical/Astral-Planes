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

        // East is reserved for "back" everywhere else in the game (see LocalPlayer.LeaveFlow.cs /
        // TrackSelectionJoinController) — don't let it also skip the intro. On some HID layouts
        // (e.g. Switch Pro) the default UI/Submit binding maps to buttonEast, so this must be
        // filtered here rather than relying on the binding itself.
        if (context.control != null && context.control.name == "buttonEast") return;

        // The settings panel (Select/View button) can be open over this same title screen —
        // a Choose/South press meant to confirm a button in that panel must not also register
        // here as "any input" and skip straight to ship selection. ShouldSuppressInput (not just
        // IsOpen) also covers the same frame the panel just closed on this exact press.
        if (SettingsPanelController.ShouldSuppressInput) return;

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