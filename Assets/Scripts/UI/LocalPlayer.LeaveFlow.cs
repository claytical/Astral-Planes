using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class LocalPlayer
{
    private Coroutine _leaveHoldCo;
    private const float HoldToLeaveSeconds = 3f;

    // Same "Leave" action (East on gamepad) means two different things depending on context:
    // instant leave during plain ship select, or a hold-to-confirm session abort mid-tutorial.
    private void HandleLeavePressed()
    {
        if (ControlTutorialDirector.Instance != null && ControlTutorialDirector.Instance.IsPrimaryTutorialRunning)
        {
            if (_leaveHoldCo == null)
                _leaveHoldCo = StartCoroutine(HoldToLeaveRoutine());
        }
        else
        {
            LeaveShipSelect();
        }
    }

    private void HandleLeaveReleased()
    {
        if (_leaveHoldCo == null) return;
        StopCoroutine(_leaveHoldCo);
        _leaveHoldCo = null;
        ControlTutorialDirector.Instance?.CancelAbortHoldUI();
    }

    private IEnumerator HoldToLeaveRoutine()
    {
        ControlTutorialDirector.Instance.BeginAbortHoldUI("Quitting", HoldToLeaveSeconds);

        float t = 0f;
        while (t < HoldToLeaveSeconds)
        {
            t += Time.unscaledDeltaTime;
            ControlTutorialDirector.Instance.UpdateAbortHoldUI(t / HoldToLeaveSeconds);
            yield return null;
        }

        _leaveHoldCo = null;
        ControlTutorialDirector.Instance.EndAbortHoldUI();
        GameFlowManager.Instance.AbortTutorialSession();
    }

    private void LeaveShipSelect()
    {
        // Guard against the brief GeneratedTrack setup window, where GameState is still
        // Selection (it flips to Playing only at the end of scene setup) but the player
        // has already confirmed and left the TrackSelection scene behind.
        if (SceneManager.GetActiveScene().name != "TrackSelection") return;
        if (GameFlowManager.Instance == null || GameFlowManager.Instance.CurrentState != GameState.Selection) return;

        ControlTutorialDirector.Instance?.UnregisterMini(this);

        if (_selection != null)
        {
            _selection.ReleasePlane();
            Destroy(_selection.gameObject);
            _selection = null;
        }

        GameFlowManager.Instance.UnregisterPlayer(this);
        Destroy(gameObject);
    }

    // Gameplay abort: holding East during Play (South-only now owns ReleaseNote, see
    // AstralPlanes.inputactions) fully quits the run back to Main, same as the GameOver
    // quit path (LocalPlayer.Input.cs:OnQuit) — unlike the tutorial's lighter
    // ReturnToJoinScreen(), a live run has an active track/dust/vehicles that need the full
    // teardown QuitToSelection() already does.
    private Coroutine _playLeaveHoldCo;

    private void HandlePlayLeavePressed()
    {
        if (_playLeaveHoldCo == null)
            _playLeaveHoldCo = StartCoroutine(HoldToAbortGameplayRoutine());
    }

    private void HandlePlayLeaveReleased()
    {
        if (_playLeaveHoldCo == null) return;
        StopCoroutine(_playLeaveHoldCo);
        _playLeaveHoldCo = null;
        _ui?.CancelAbortHold();
    }

    private IEnumerator HoldToAbortGameplayRoutine()
    {
        _ui?.BeginAbortHold("Giving Up", HoldToLeaveSeconds);

        float t = 0f;
        while (t < HoldToLeaveSeconds)
        {
            t += Time.unscaledDeltaTime;
            _ui?.UpdateAbortHold(t / HoldToLeaveSeconds);
            yield return null;
        }

        _playLeaveHoldCo = null;
        _ui?.EndAbortHold();
        GameFlowManager.Instance.QuitToSelection();
    }
}
