using UnityEngine;
using UnityEngine.EventSystems;

// Main.unity settings panel: lets the player revisit their Living Score publish choice at any
// time. Modeled on LivingScorePanel.cs (same two-button/highlight pattern), but:
//  - reads TelemetryConsent's already-decided state to pick which button is pre-highlighted,
//    instead of always defaulting to "Do Not Publish" (that panel is a first-time prompt where
//    nothing has been decided yet; this one is a revisit, so the current choice should show).
//  - doesn't need LivingScorePanel's IsAwaitingChoice/ConfirmCurrentSelection guard machinery —
//    that exists only to avoid racing a LocalPlayer's raw Choose/South handler in TrackFinished's
//    GameState.GameOver, and Main.unity has no LocalPlayer until a player joins; its EventSystem
//    already resolves gamepad Submit through Unity's own default UI actions independent of any
//    per-player action map.
public class PublishSettingsPanel : MonoBehaviour
{
    [SerializeField] private GameObject publishButton;
    [SerializeField] private GameObject doNotPublishButton;

    private void OnEnable()
    {
        bool publishOn = TelemetryConsent.HasDecided && TelemetryConsent.PublishFlightPlansEnabled;
        var target = publishOn ? publishButton : doNotPublishButton;
        if (target != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(target);
    }

    public void OnPublishClicked()
    {
        TelemetryConsent.PublishFlightPlansEnabled = true;
        TelemetryConsent.HasDecided = true;
        FindAnyObjectByType<MazeTelemetryUploader>()?.FlushPendingNow();

        GetComponentInParent<SettingsPanelController>()?.Close();
    }

    public void OnDoNotPublishClicked()
    {
        bool wasOn = TelemetryConsent.PublishFlightPlansEnabled;
        TelemetryConsent.PublishFlightPlansEnabled = false;
        TelemetryConsent.HasDecided = true;
        if (wasOn) FindAnyObjectByType<MazeTelemetryUploader>()?.RemovePublishedFlightPlans();

        GetComponentInParent<SettingsPanelController>()?.Close();
    }
}
