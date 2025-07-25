using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
public class IntroInputHandler : MonoBehaviour
{
    public GameObject quoteText;
    public GameObject gameModeSelect;
    public GameObject selectedGameMode;
    private bool inputReceived = false;

    public void OnAnyInput(InputAction.CallbackContext context)
    {
        if (inputReceived || !context.performed) return;

        inputReceived = true;

        // Hide quote
        quoteText.SetActive(false);
        //AUTOSTART THE RIVER
        GameFlowManager.Instance.SetSelectedMode("The River");
        GameFlowManager.Instance.StartShipSelectionPhase();
        PlaneSelection();

//        ShowMenuClean();        
    }
    public void ShowMenuClean()
    {
        gameModeSelect.SetActive(true);
        quoteText.SetActive(false);
        StartCoroutine(ResetUISelection());
    }

    private IEnumerator ResetUISelection()
    {
        // Clear any current selection (prevents input carry-over)
        EventSystem.current.SetSelectedGameObject(null);
        yield return null; // Wait one frame

        // Now safely select the first UI element
        EventSystem.current.SetSelectedGameObject(selectedGameMode);
    }
    void Start()
    {
        // Optionally disable buttons initially
        gameModeSelect.SetActive(false);
    }
    public void PlaneSelection()
    {
        //TODO: Differentiate between plane and mode/track selection if needed
        SceneManager.LoadScene("TrackSelection");
    }

    public void TrackSelection()
    {
        SceneManager.LoadScene("TrackSelection");
    }
}