using UnityEngine;
using UnityEngine.EventSystems;

// TrackFinished scene: the "Living Score" publish-consent prompt. Wired to the panel's
// Publish/Do Not Publish buttons via their OnClick UnityEvents.
public class LivingScorePanel : MonoBehaviour
{
    // The scene's EventSystem already names this as m_FirstSelected, but that only applies at
    // EventSystem startup — this panel starts inactive and is enabled later by
    // SolarSystemRecordDisplay, well after that, so nothing is ever actually selected unless we
    // set it ourselves here. Without a selection, gamepad Submit (South) has no target and does
    // nothing until focus is set some other way. Defaults to Do Not Publish (opt-in, not opt-out).
    [SerializeField] private GameObject defaultSelected;

    // LocalPlayer.HandleConfirm's GameState.GameOver case restarts the run on the raw
    // Choose/South action the instant it's pressed, independent of the UI event system — it
    // checks this to avoid racing/preempting a Publish/Do Not Publish click with a restart
    // while this panel is still waiting on the player's choice.
    //
    // The same physical Choose/South press can drive both this panel's button (via the UI
    // event system) and LocalPlayer's raw action callback in the same frame, in either order —
    // so IsAwaitingChoice alone isn't enough: if the click's SetActive(false) (and this
    // GameObject's OnDisable) happens to run before LocalPlayer's check, the guard would
    // already read false. _choiceMadeFrame keeps the guard up for the rest of that frame
    // regardless of ordering, and stops blocking on the next real, separate press.
    public static bool IsAwaitingChoice { get; private set; }
    private static int _choiceMadeFrame = -1;

    private static bool JustMadeChoiceThisFrame => Time.frameCount == _choiceMadeFrame;

    public static bool ShouldSuppressConfirm => IsAwaitingChoice || JustMadeChoiceThisFrame;

    private void OnEnable()
    {
        IsAwaitingChoice = true;
        if (defaultSelected != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(defaultSelected);
    }

    private void OnDisable() => IsAwaitingChoice = false;

    // The scene's EventSystem resolves gamepad Submit/Cancel through Unity's built-in default UI
    // actions, which bind them to the semantic */{Submit} and */{Cancel} composites rather than
    // raw buttonSouth/buttonEast — Unity resolves those per controller layout, and for a Switch
    // controller that follows Nintendo's own (physically swapped) confirm/back convention. That
    // makes South never reach a Button's OnClick here, while every other confirm action in this
    // game is hard-bound to raw buttonSouth via AstralPlanes.inputactions' "Choose" action and
    // stays consistent across controllers. So LocalPlayer.HandleConfirm calls this directly off
    // that same "Choose" press instead of relying on the EventSystem's own Submit routing —
    // Navigate (stick/d-pad), unaffected by the Submit/Cancel remap, still moves the selection.
    public static void ConfirmCurrentSelection()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected == null) return;
        ExecuteEvents.Execute(selected, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
    }

    public void OnPublishClicked()
    {
        _choiceMadeFrame = Time.frameCount;
        TelemetryConsent.PublishFlightPlansEnabled = true;
        TelemetryConsent.HasDecided = true;
        FindAnyObjectByType<MazeTelemetryUploader>()?.FlushPendingNow();
    }

    public void OnDoNotPublishClicked()
    {
        _choiceMadeFrame = Time.frameCount;
        TelemetryConsent.PublishFlightPlansEnabled = false;
        TelemetryConsent.HasDecided = true;
    }
}
