using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Main.unity settings: the Global/OpenSettings action (gamepad Select/View/Share/Minus) toggles
// the "Publish Settings" modal, shown through the generic ModalMenu. The Global action map is
// enabled directly off the InputActionAsset (not through any PlayerInput), because Main.unity
// is the title/boot scene and often has zero LocalPlayer instances joined yet.
//
// Lets the player revisit their Living Score publish choice at any time; the current choice is
// pre-highlighted (unlike LivingScorePanel's first-time prompt, which defaults to Don't Publish).
//
// Input that the title screen would otherwise react to (IntroInputHandler, PhaseLibraryCarousel,
// PreviewBeatToggle) is gated on ModalMenu.ShouldSuppressInput, which also covers the frame the
// modal closed on so the confirming South press can't fall through.
public class SettingsPanelController : MonoBehaviour
{
    [SerializeField] private InputActionAsset actions;

    private InputAction _openSettingsAction;
    private ModalMenuSpec _spec;

    private void Awake()
    {
        if (actions == null)
        {
            Debug.LogWarning("[SettingsPanelController] No InputActionAsset assigned.", this);
            return;
        }

        _openSettingsAction = actions.FindActionMap("Global")?.FindAction("OpenSettings");
        if (_openSettingsAction == null)
        {
            Debug.LogWarning("[SettingsPanelController] Global/OpenSettings action not found.", this);
            return;
        }

        _openSettingsAction.started += OnOpenSettingsPressed;
        _openSettingsAction.Enable();
    }

    private void OnDestroy()
    {
        if (_openSettingsAction != null)
            _openSettingsAction.started -= OnOpenSettingsPressed;
        if (_spec != null && ModalMenu.Current == _spec) ModalMenu.Close();
    }

    private void OnOpenSettingsPressed(InputAction.CallbackContext ctx)
    {
        if (ModalMenu.IsOpen)
        {
            if (ModalMenu.Current == _spec) ModalMenu.Close();
            return; // another modal owns the screen
        }
        if (ModalMenu.ShouldSuppressInput) return;

        bool publishOn = TelemetryConsent.HasDecided && TelemetryConsent.PublishFlightPlansEnabled;
        _spec = new ModalMenuSpec
        {
            title = "Publish Settings",
            description = "Choose whether your flight records are published to the Living Score.",
            defaultIndex = publishOn ? 1 : 0,
            options = new List<ModalMenuOption>
            {
                new ModalMenuOption("Don't Publish", SetDoNotPublish),
                new ModalMenuOption("Yes, Publish My Flight Records", SetPublish),
            },
        };
        ModalMenu.Show(_spec);
    }

    private static void SetPublish()
    {
        TelemetryConsent.PublishFlightPlansEnabled = true;
        TelemetryConsent.HasDecided = true;
        FindAnyObjectByType<MazeTelemetryUploader>()?.FlushPendingNow();
    }

    private static void SetDoNotPublish()
    {
        bool wasOn = TelemetryConsent.PublishFlightPlansEnabled;
        TelemetryConsent.PublishFlightPlansEnabled = false;
        TelemetryConsent.HasDecided = true;
        if (wasOn) FindAnyObjectByType<MazeTelemetryUploader>()?.RemovePublishedFlightPlans();
    }
}
