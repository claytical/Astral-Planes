using UnityEngine;
using UnityEngine.InputSystem;

// Arbitrates a single physical keyboard Escape press between two independent listeners that
// both react to it: the existing per-player Leave/abort hold-to-confirm flows
// (LocalPlayer.LeaveFlow.cs, bound via AstralPlanes.inputactions' Selection/Play "Leave"
// action — ungrouped, so it fires regardless of control scheme) and this new global
// quit-confirmation modal.
//
// Deliberately bypasses AstralPlanes.inputactions/PlayerInput entirely and polls
// Keyboard.current directly (same precedent as the keyboard fallback code in
// LocalPlayer.Input.cs) — this must work identically whether zero, one, or several
// LocalPlayer/PlayerInput instances exist, and must not fight per-player/per-scene action-map
// enable-disable or control-scheme switching.
//
// Resolution: hold Escape to the existing 3-second threshold and Leave's own hold coroutine
// (if one is running) keeps doing exactly what it does today — this listener recognizes the
// same threshold independently and stays out of the way. A quick tap-and-release instead opens
// this modal, UNLESS the same press already fired the one leave path that has no hold at all
// (the non-tutorial ship-select instant LeaveShipSelect()) — see LocalPlayer.JustLeftShipSelectInstantly.
public class GlobalEscapeQuitListener : MonoBehaviour
{
    private const float HoldThresholdSeconds = 3f;

    [SerializeField] private QuitConfirmationController modal;

    private bool _tracking;
    private float _pressStartUnscaledTime;
    private bool _instantLeaveClaimedThisPress;

    private void Update()
    {
        if (Keyboard.current == null || modal == null) return;

        var escape = Keyboard.current.escapeKey;

        if (escape.wasPressedThisFrame)
        {
            _tracking = true;
            _pressStartUnscaledTime = Time.unscaledTime;
            // Snapshot now — LeaveShipSelect() (if it fires at all for this press) runs
            // synchronously inside the same started-callback this same physical press triggers,
            // so it's already reflected in this same frame.
            _instantLeaveClaimedThisPress = LocalPlayer.JustLeftShipSelectInstantly;
        }

        if (!_tracking) return;

        if (escape.wasReleasedThisFrame)
        {
            _tracking = false;

            float elapsed = Time.unscaledTime - _pressStartUnscaledTime;
            if (elapsed >= HoldThresholdSeconds) return; // genuine hold — Leave's own coroutine (if any) already handled it
            if (_instantLeaveClaimedThisPress) return;    // this same tap already fired the instant, no-hold leave
            if (SettingsPanelController.ShouldSuppressInput) return;
            if (LivingScorePanel.ShouldSuppressConfirm) return;

            modal.Toggle();
        }
    }
}
