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
    // actions (*/{Submit}, */{Cancel} usages), not through this game's "Choose" action. The game
    // now follows Nintendo's A-confirms convention on Switch everywhere (PadSemantics), so the
    // two agree on every controller, but LocalPlayer.HandleConfirm still calls this directly off
    // the "Choose" press so confirmation never depends on the EventSystem's Submit routing.
    // Navigate (stick/d-pad) still moves the selection.
    public static void ConfirmCurrentSelection()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected == null) return;
        ExecuteEvents.Execute(selected, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
    }

    public void OnPublishClicked()
    {
        _choiceMadeFrame = Time.frameCount;
        TelemetryConsent.Decide(true);
        FindAnyObjectByType<MazeTelemetryUploader>()?.FlushPendingNow();
    }

    public void OnDoNotPublishClicked()
    {
        _choiceMadeFrame = Time.frameCount;
        TelemetryConsent.Decide(false);
    }
}
