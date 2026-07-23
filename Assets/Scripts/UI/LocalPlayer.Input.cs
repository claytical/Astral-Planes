using UnityEngine;
using UnityEngine.InputSystem;

public partial class LocalPlayer
{
    public AudioClip clickFx;
    public float friction = 0.5f;

    [Header("Gameplay Input")]
    private Vector2 _virtualStick;      // accumulated, clamped to radius 1

    [Header("Keyboard")]
    [Tooltip("Degrees per second the heading rotates toward the held arrow direction.")]
    [SerializeField] private float keyboardTurnRateDeg = 270f;
    [Tooltip("Rate at which stick magnitude ramps from 0 to 1 when an arrow key is held (units/sec).")]
    [SerializeField] private float keyboardAccelRate = 5f;
    [Tooltip("Exponential decay rate to 0 for the virtual stick when no input is held.")]
    [SerializeField] private float stickDecayPerSec = 2.5f;

    private bool _gamepadDriving; // true while a gamepad stick is (or was last) driving _virtualStick

    public Vector2 GetMoveInput()
    {
        // During gameplay, _virtualStick is maintained by FixedUpdate.
        // During the tutorial (before the vehicle exists) the Play action map isn't active,
        // so we read the device state directly instead.
        if (plane != null) return _virtualStick;

        if (_playerInput != null)
        {
            foreach (var device in _playerInput.devices)
            {
                if (device is Gamepad gp)
                    return Vector2.ClampMagnitude(gp.leftStick.ReadValue(), 1f);
            }
        }

        if (Keyboard.current != null)
        {
            var dir = Vector2.zero;
            if (Keyboard.current.upArrowKey.isPressed || Keyboard.current.wKey.isPressed)    dir.y += 1f;
            if (Keyboard.current.downArrowKey.isPressed || Keyboard.current.sKey.isPressed)  dir.y -= 1f;
            if (Keyboard.current.leftArrowKey.isPressed || Keyboard.current.aKey.isPressed)  dir.x -= 1f;
            if (Keyboard.current.rightArrowKey.isPressed || Keyboard.current.dKey.isPressed) dir.x += 1f;
            return Vector2.ClampMagnitude(dir, 1f);
        }

        return Vector2.zero;
    }

    public float GetThrustInput()
    {
        // Same issue: Thrust is in the Play map. Read the device directly during tutorial.
        if (_playerInput != null)
        {
            foreach (var device in _playerInput.devices)
            {
                if (device is Gamepad gp)
                    return gp.rightTrigger.ReadValue();
            }
        }
        return Keyboard.current != null && Keyboard.current.spaceKey.isPressed ? 1f : 0f;
    }

    public bool GetChooseInput() => _confirmAction?.IsPressed() ?? false;

    private void FixedUpdate()
    {
        if (plane == null) return;

        float dt = Time.fixedDeltaTime;

        // Read raw stick/keys
        Vector2 raw = (_moveAction != null) ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        raw = Vector2.ClampMagnitude(raw, 1f);

        bool isGamepad = _moveAction?.activeControl?.device is Gamepad;

        if (isGamepad)
        {
            _virtualStick = raw;
            _gamepadDriving = true;
        }
        else
        {
            _gamepadDriving = false;

            if (Keyboard.current != null)
            {
                Vector2 arrowTarget = Vector2.zero;
                if (Keyboard.current.upArrowKey.isPressed)    arrowTarget.y += 1f;
                if (Keyboard.current.downArrowKey.isPressed)  arrowTarget.y -= 1f;
                if (Keyboard.current.leftArrowKey.isPressed)  arrowTarget.x -= 1f;
                if (Keyboard.current.rightArrowKey.isPressed) arrowTarget.x += 1f;

                if (arrowTarget.sqrMagnitude > 0.0001f)
                {
                    arrowTarget.Normalize();
                    float tgtAngle = Mathf.Atan2(arrowTarget.y, arrowTarget.x) * Mathf.Rad2Deg;
                    float curMag   = _virtualStick.magnitude;
                    float curAngle = curMag > 0.0001f
                        ? Mathf.Atan2(_virtualStick.y, _virtualStick.x) * Mathf.Rad2Deg
                        : tgtAngle;
                    float newAngle = Mathf.MoveTowardsAngle(curAngle, tgtAngle, keyboardTurnRateDeg * dt);
                    float newMag   = Mathf.MoveTowards(curMag, 1f, keyboardAccelRate * dt);
                    float rad      = newAngle * Mathf.Deg2Rad;
                    _virtualStick  = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * newMag;
                }
                else
                {
                    float k = Mathf.Exp(-stickDecayPerSec * dt);
                    _virtualStick *= k;
                    if (_virtualStick.sqrMagnitude < 0.0001f) _virtualStick = Vector2.zero;
                }
            }
        }

        plane.Move(_virtualStick * friction);
    }

    private void Update()
    {
        if (plane == null) return;

        if (Keyboard.current != null)
        {
            // Space held = boost.
            if (Keyboard.current.spaceKey.wasPressedThisFrame)
                plane.TurnOnBoost(1f);
            else if (Keyboard.current.spaceKey.wasReleasedThisFrame)
                plane.TurnOffBoost();

            // Enter / numpad Enter = release note (fallback only when no Input Action is bound).
            if (_releaseAction == null)
            {
                if (Keyboard.current.enterKey.wasPressedThisFrame ||
                    Keyboard.current.numpadEnterKey.wasPressedThisFrame)
                {
                    plane.TryReleaseQueuedNote();
                    plane.SetReleaseButtonHeld(true);
                }
                if (Keyboard.current.enterKey.wasReleasedThisFrame ||
                    Keyboard.current.numpadEnterKey.wasReleasedThisFrame)
                    plane.SetReleaseButtonHeld(false);
            }
        }
    }

    public void OnThrust(InputValue value)
    {
        float v = value.Get<float>();
        if (v > 0) plane?.TurnOnBoost(v);
        else plane?.TurnOffBoost();
        if (GameFlowManager.VerboseLogging) Debug.Log($"[VEHICLE] Thrusting stopped at {v}");

    }

    public void OnMouseMove(InputValue value)
    {
        // Mouse delta is now read directly in Update() via Mouse.current.delta.
        // This callback is kept to suppress Input System missing-binding warnings.
    }

    public void OnQuit(InputValue value)
    {
        if (GameFlowManager.Instance.CurrentState == GameState.GameOver)
        {
            GameFlowManager.Instance.QuitToSelection();
            Destroy(gameObject);
        }
    }

    public void OnNext(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);

        if (_selection != null)
        {
            _selection.NextVehicle();
            NotifySelectionNavigatedOnce();
        }

        OnNavigateNext?.Invoke();
    }

    public void OnPrevious(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);

        if (_selection != null)
        {
            _selection.PreviousVehicle();
            NotifySelectionNavigatedOnce();
        }

        OnNavigatePrevious?.Invoke();
    }
}
