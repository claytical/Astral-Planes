using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class LocalPlayer
{
    private Coroutine _leaveHoldCo;
    private const float HoldToLeaveSeconds = 3f;

    // Leaving the session from the vehicle stage with 2+ players joined needs a short hold so a
    // casual "undo" tap can never knock a player out of a multiplayer session.
    private Coroutine _leaveSelectHoldCo;
    private const float HoldToLeaveSelectSeconds = 1.5f;

    // The tutorial abort-hold prompt is a single shared UI; only hide it when no player's
    // abort hold is still running.
    private static int _activeAbortHolds;

    // GlobalEscapeQuitListener needs to know, at the moment a physical Escape release would
    // otherwise open the quit-confirmation modal, whether this same press already fired the
    // instant (no-hold) leave below — so it doesn't pop the quit modal on top of a leave that
    // already happened. Only set here, never for starting a hold timer, so an early-released
    // hold (which today does nothing) still correctly falls through to the quit modal.
    private static int _instantLeaveFrame = -1;
    public static bool JustLeftShipSelectInstantly => Time.frameCount == _instantLeaveFrame;

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
            // Back is a per-player undo, one step at a time:
            //   ready -> groove-style stage -> vehicle stage -> (hold) leave the session.
            if (_isReady && _selection != null)
            {
                _instantLeaveFrame = Time.frameCount; // keep a same-press Esc from opening the quit modal
                _sfxSource?.PlayOneShot(clickFx);
                _selection.Unconfirm();
                ResetReady();
                return;
            }
            if (_selection != null && _selection.StepBackStage())
            {
                _instantLeaveFrame = Time.frameCount;
                _sfxSource?.PlayOneShot(clickFx);
                return;
            }

            int joined = GameFlowManager.Instance?.SessionState?.Players?.Count ?? 1;
            if (joined >= 2)
            {
                // Swallow the tap so Esc doesn't pop the quit modal; leaving needs a hold.
                _instantLeaveFrame = Time.frameCount;
                if (_leaveSelectHoldCo == null)
                    _leaveSelectHoldCo = StartCoroutine(HoldToLeaveSelectRoutine());
                return;
            }
            LeaveShipSelect();
        }
    }

    private IEnumerator HoldToLeaveSelectRoutine()
    {
        float t = 0f;
        while (t < HoldToLeaveSelectSeconds)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        _leaveSelectHoldCo = null;
        LeaveShipSelect();
    }

    private void HandleLeaveReleased()
    {
        if (_leaveSelectHoldCo != null)
        {
            StopCoroutine(_leaveSelectHoldCo);
            _leaveSelectHoldCo = null;
        }

        if (_leaveHoldCo == null) return;
        StopCoroutine(_leaveHoldCo);
        _leaveHoldCo = null;
        _activeAbortHolds = Mathf.Max(0, _activeAbortHolds - 1);
        if (_activeAbortHolds == 0)
            ControlTutorialDirector.Instance?.CancelAbortHoldUI();
    }

    private IEnumerator HoldToLeaveRoutine()
    {
        _activeAbortHolds++;
        ControlTutorialDirector.Instance.BeginAbortHoldUI("Quitting", HoldToLeaveSeconds);

        float t = 0f;
        while (t < HoldToLeaveSeconds)
        {
            t += Time.unscaledDeltaTime;
            ControlTutorialDirector.Instance.UpdateAbortHoldUI(t / HoldToLeaveSeconds);
            yield return null;
        }

        _leaveHoldCo = null;
        _activeAbortHolds = Mathf.Max(0, _activeAbortHolds - 1);
        ControlTutorialDirector.Instance.EndAbortHoldUI();
        GameFlowManager.Instance.AbortTutorialForPlayer(this);
    }

    private void LeaveShipSelect()
    {
        // Guard against the brief GeneratedTrack setup window, where GameState is still
        // Selection (it flips to Playing only at the end of scene setup) but the player
        // has already confirmed and left the TrackSelection scene behind.
        if (SceneManager.GetActiveScene().name != "TrackSelection") return;
        if (GameFlowManager.Instance == null || GameFlowManager.Instance.CurrentState != GameState.Selection) return;

        _instantLeaveFrame = Time.frameCount;
        LeaveSessionNow();
    }

    /// <summary>Removes this player from the session (no scene/state guards; callers decide when that is appropriate).</summary>
    public void LeaveSessionNow()
    {
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
