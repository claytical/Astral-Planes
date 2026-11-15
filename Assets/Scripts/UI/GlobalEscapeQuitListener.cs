using UnityEngine;
using UnityEngine.InputSystem;

// Arbitrates a single physical keyboard Escape press between two independent listeners that
// both react to it: the existing per-player Leave/abort hold-to-confirm flows
// (LocalPlayer.LeaveFlow.cs, bound via AstralPlanes.inputactions' Selection/Play "Leave"
// action — ungrouped, so it fires regardless of control scheme) and the quit prompt
// (QuitPrompt, shown through the generic ModalMenu).
//
// Added at runtime to GameFlowManager's DontDestroyOnLoad object (GameFlowManager.QuitFlow), so
// it works in every scene. Deliberately bypasses AstralPlanes.inputactions/PlayerInput entirely
// and polls Keyboard.current directly — this must work identically whether zero, one, or several
// LocalPlayer/PlayerInput instances exist.
//
// Resolution: hold Escape to the existing 3-second threshold and Leave's own hold coroutine
// (if one is running) keeps doing exactly what it does today — this listener recognizes the
// same threshold independently and stays out of the way. A quick tap-and-release instead opens
// the quit prompt, UNLESS the same press already fired the one leave path that has no hold at all
// (the non-tutorial ship-select instant LeaveShipSelect()) — see LocalPlayer.JustLeftShipSelectInstantly —
// or a modal was already open when Escape went down (ModalMenu treats that press as cancel).
public class GlobalEscapeQuitListener : MonoBehaviour
{
    private const float HoldThresholdSeconds = 3f;

    private bool _tracking;
    private float _pressStartUnscaledTime;
    private bool _instantLeaveClaimedThisPress;
    private bool _modalOpenAtPress;

    private void Update()
    {
        if (Keyboard.current == null) return;

        var escape = Keyboard.current.escapeKey;

        if (escape.wasPressedThisFrame)
        {
            _tracking = true;
            _pressStartUnscaledTime = Time.unscaledTime;
            // Snapshot now — LeaveShipSelect() (if it fires at all for this press) runs
            // synchronously inside the same started-callback this same physical press triggers,
            // so it's already reflected in this same frame.
            _instantLeaveClaimedThisPress = LocalPlayer.JustLeftShipSelectInstantly;
            // ModalMenu may already have closed on this press (Update order), so ShouldSuppressInput
            // (which covers the closing frame) is the right snapshot, not IsOpen alone.
            _modalOpenAtPress = ModalMenu.ShouldSuppressInput;
        }

        if (!_tracking) return;

        if (escape.wasReleasedThisFrame)
        {
            _tracking = false;

            float elapsed = Time.unscaledTime - _pressStartUnscaledTime;
            if (elapsed >= HoldThresholdSeconds) return; // genuine hold — Leave's own coroutine (if any) already handled it
            if (_instantLeaveClaimedThisPress) return;    // this same tap already fired the instant, no-hold leave
            if (_modalOpenAtPress) return;                // that press cancelled a modal
            if (ModalMenu.IsOpen) return;
            if (LivingScorePanel.ShouldSuppressConfirm) return;

            QuitPrompt.Show();
        }
    }
}
