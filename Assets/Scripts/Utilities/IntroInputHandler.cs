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

        // The back button (East; South on a native Switch Pro, see PadSemantics) is reserved for
        // "back" everywhere else in the game (LocalPlayer.LeaveFlow.cs /
        // TrackSelectionJoinController) — don't let it also skip the intro.
        if (PadSemantics.IsBackControl(context.control)) return;

        // The settings panel (Select/View button) can be open over this same title screen —
        // a Choose/South press meant to confirm a button in that panel must not also register
        // here as "any input" and skip straight to ship selection. ShouldSuppressInput (not just
        // IsOpen) also covers the same frame the panel just closed on this exact press.
        if (ModalMenu.ShouldSuppressInput) return;

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