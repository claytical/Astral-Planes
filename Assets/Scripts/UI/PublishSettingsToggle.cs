using UnityEngine;
using UnityEngine.UI;

// Main.unity settings option letting the player revisit their Living Score publish choice.
// Only meaningful once the player has been through the Living Score prompt at least once
// (TelemetryConsent.HasDecided) — hides itself otherwise.
public class PublishSettingsToggle : MonoBehaviour
{
    [SerializeField] private Toggle toggle;

    private void Start()
    {
        if (!TelemetryConsent.HasDecided)
        {
            gameObject.SetActive(false);
            return;
        }

        if (toggle != null) toggle.SetIsOnWithoutNotify(TelemetryConsent.PublishFlightPlansEnabled);
    }

    // Wire this to the Toggle's OnValueChanged in the Inspector.
    public void OnTogglePublish(bool on)
    {
        bool wasOn = TelemetryConsent.PublishFlightPlansEnabled;
        TelemetryConsent.PublishFlightPlansEnabled = on;

        if (wasOn && !on)
            FindAnyObjectByType<MazeTelemetryUploader>()?.RemovePublishedFlightPlans();
        else if (!wasOn && on)
            FindAnyObjectByType<MazeTelemetryUploader>()?.FlushPendingNow();
    }
}
