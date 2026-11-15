using System.Linq;
using UnityEngine.InputSystem;

/// <summary>
/// Gamepad Select (Play/Pause) opens the soft-pause menu (<see cref="PauseMenuController"/>).
/// Each player subscribes through their own PlayerInput, so the press always comes from a device
/// that player owns (the Steam Deck's duplicate raw-HID gamepad never joins a player).
/// </summary>
public partial class LocalPlayer
{
    private InputAction _pauseAction;
    private System.Action<InputAction.CallbackContext> _onPauseStarted;

    private void BindPauseAction()
    {
        _pauseAction = _playerInput.actions.FindAction("Play/Pause", throwIfNotFound: false);
        if (_pauseAction == null) return;
        _onPauseStarted = ctx => PauseMenuController.Toggle(this);
        _pauseAction.started += _onPauseStarted;
    }

    private void UnbindPauseAction()
    {
        if (_pauseAction != null && _onPauseStarted != null)
            _pauseAction.started -= _onPauseStarted;
    }

    /// <summary>This player's own gamepad, used to drive the pause menu.</summary>
    public Gamepad PrimaryGamepad() =>
        _playerInput != null ? _playerInput.devices.OfType<Gamepad>().FirstOrDefault() : null;
}
