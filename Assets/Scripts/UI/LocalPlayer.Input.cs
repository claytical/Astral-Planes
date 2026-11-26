using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class LocalPlayer
{
    public AudioClip clickFx;
    public float friction = 0.5f;

    [Header("Gameplay Input")]
    private Vector2 _virtualStick;      // desired travel direction fed to Vehicle.Move, radius ≤ 1

    [Header("Keyboard (rotational steering)")]
    [Tooltip("Degrees per second A/D (or Left/Right) rotate the keyboard player's nose. Steering only: W/S drift, Shift boosts.")]
    [SerializeField] private float keyboardTurnRateDeg = 240f;

    [Header("Mouse (fly toward cursor)")]
    [Tooltip("World-unit radius around the cursor inside which the ship stops steering and coasts.")]
    [SerializeField] private float mouseArriveRadius = 0.6f;
    [Tooltip("Seconds of travel added to the arrive radius: radius = max(mouseArriveRadius, speed × this). " +
             "Lets a fast (boosting) ship let go before it overshoots the cursor.")]
    [SerializeField] private float mouseArriveSpeedScale = 0.12f;
    [Tooltip("After arriving, steering resumes only once the cursor is this many arrive-radii away (hysteresis).")]
    [SerializeField] private float mouseRearmMultiplier = 2f;
    private bool _mouseArrived;

    // Keyboard scheme heading (degrees, 0 = +X). Seeded from the vehicle's nose whenever the
    // vehicle changes or the scheme switches to Keyboard.
    private float _kbHeadingDeg;
    private Vehicle _kbHeadingPlane;
    private float _kbTurnAxis;              // last Turn reading (for tutorial "steering" intent)

    private bool _kbReverseHeld;            // S/Down held last tick (flip the nose on a new press)

    private InputAction _turnAction;
    private InputAction _driveAction;       // Keyboard: W/Up = +1 (forward), S/Down = -1 (turn around)
    private InputAction _thrustAction;      // Keyboard: Shift; Mouse: left click — boost
    private bool _kbmThrustHeld;            // KBM boost currently on (TurnOnBoost sent)

    // ---- Device / scheme queries -----------------------------------------------------------

    /// <summary>True when this player is the keyboard &amp; mouse player (paired to a Keyboard).</summary>
    public bool IsKbmPlayer => _playerInput != null && _playerInput.devices.Any(d => d is Keyboard);

    /// <summary>The keyboard player's active scheme. Only meaningful when <see cref="IsKbmPlayer"/>.</summary>
    public KbmScheme ActiveKbmScheme =>
        _playerInput != null && _playerInput.currentControlScheme == ControlPrefs.MouseSchemeName
            ? KbmScheme.Mouse
            : KbmScheme.Keyboard;

    private Keyboard PairedKeyboard =>
        _playerInput != null ? _playerInput.devices.OfType<Keyboard>().FirstOrDefault() : null;

    private Mouse PairedMouse =>
        _playerInput != null ? _playerInput.devices.OfType<Mouse>().FirstOrDefault() : null;

    private void BindKbmInput()
    {
        if (_playerInput == null) return;
        _turnAction   = _playerInput.actions.FindAction("Play/Turn", throwIfNotFound: false);
        _driveAction  = _playerInput.actions.FindAction("Play/Drive", throwIfNotFound: false);
        _thrustAction = _playerInput.actions.FindAction("Play/Thrust", throwIfNotFound: false);
        ControlPrefs.Changed += OnKbmSchemeChanged;
    }

    private void UnbindKbmInput() => ControlPrefs.Changed -= OnKbmSchemeChanged;

    // Settings → Keyboard Controls changed: switch the joined keyboard player live.
    private void OnKbmSchemeChanged(KbmScheme scheme)
    {
        if (!IsKbmPlayer) return;
        var kb = PairedKeyboard;
        var mouse = PairedMouse;
        if (scheme == KbmScheme.Mouse && mouse == null) return; // Mouse scheme needs a mouse

        string name = ControlPrefs.NameOf(scheme);
        if (_playerInput.currentControlScheme == name) return;

        if (_kbmThrustHeld) { _kbmThrustHeld = false; plane?.TurnOffBoost(); }
        if (mouse != null) _playerInput.SwitchCurrentControlScheme(name, kb, mouse);
        else _playerInput.SwitchCurrentControlScheme(name, kb);
        _kbHeadingPlane = null; // re-seed the keyboard heading from the current nose
        if (scheme != KbmScheme.Keyboard) plane?.ClearHeadingOverride();
    }

    // ---- Tutorial getters ------------------------------------------------------------------

    public Vector2 GetMoveInput()
    {
        if (IsKbmPlayer) return GetKbmMoveIntent();

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
        return Vector2.zero;
    }

    // Steering *intent* for the keyboard player (tutorial gates / live highlight), not the
    // raw travel direction: keyboard = turn or thrust keys held, mouse = cursor away from the ship.
    private Vector2 GetKbmMoveIntent()
    {
        if (ActiveKbmScheme == KbmScheme.Mouse)
        {
            if (plane != null) return _virtualStick;
            var mouse = PairedMouse;
            return mouse != null ? Vector2.ClampMagnitude(mouse.delta.ReadValue() * 0.1f, 1f) : Vector2.zero;
        }

        var kb = PairedKeyboard;
        if (kb == null) return Vector2.zero;
        float x = 0f, y = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  x -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed)     y += 1f; // W/S are the only (non-boost) thrust
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed)   y -= 1f;
        return Vector2.ClampMagnitude(new Vector2(x, y), 1f);
    }

    public float GetThrustInput()
    {
        // Thrust is in the Play map. Read this player's own devices directly during the tutorial.
        if (_playerInput != null)
        {
            foreach (var device in _playerInput.devices)
            {
                if (device is Gamepad gp)
                    return gp.rightTrigger.ReadValue();
            }
        }

        if (IsKbmPlayer)
        {
            if (ActiveKbmScheme == KbmScheme.Mouse)
            {
                var mouse = PairedMouse;
                return mouse != null && mouse.leftButton.isPressed ? 1f : 0f;
            }
            var kb = PairedKeyboard;
            return kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) ? 1f : 0f;
        }
        return 0f;
    }

    public bool GetChooseInput() => _confirmAction?.IsPressed() ?? false;

    /// <summary>Play-map ReleaseNote held — the control sandbox's hold-to-skip.</summary>
    public bool IsReleaseHeld() => _releaseAction?.IsPressed() ?? false;

    // ---- Steering --------------------------------------------------------------------------

    private void FixedUpdate()
    {
        if (plane == null) return;

        float dt = Time.fixedDeltaTime;

        if (IsKbmPlayer)
        {
            if (ActiveKbmScheme == KbmScheme.Mouse) SteerTowardCursor();
            else SteerKeyboardRotational(dt);
        }
        else
        {
            // Gamepad: absolute-direction steering from the stick. (Previously a centred stick
            // had no activeControl and fell into the keyboard decay path — no longer.)
            Vector2 raw = (_moveAction != null) ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
            _virtualStick = Vector2.ClampMagnitude(raw, 1f);
        }

        if (!_inputLocked && !ModalMenu.IsOpen) plane.Move(_virtualStick * friction);
    }

    // Keyboard scheme: rotational steering, asteroids-style.
    //   A/D (Left/Right) only rotate the nose. No thrust: the ship keeps drifting the way it was
    //     going (coast brake as usual) and the nose stays where it was left.
    //   W/Up drifts forward along the nose at normal (non-boost) speed.
    //   S/Down turns the ship around (nose flips 180° on the press, at the vehicle's capped turn
    //     rate) and drifts that way while held.
    //   Shift boosts along the nose (see Update), with or without W/S.
    // The nose is driven by Vehicle.SetHeadingOverride for as long as the keyboard scheme owns the
    // plane, so it doesn't swing back to face velocity after a turn or a bounce.
    //
    // While driving or boosting the input is the heading, never zero: with zero input,
    // Vehicle.UpdateMovementAndFuel boosts along _lastNonZeroInput, which goes stale after drifts
    // and bounces.
    private void SteerKeyboardRotational(float dt)
    {
        bool usable = !_inputLocked && !ModalMenu.IsOpen;
        _kbTurnAxis = (_turnAction != null && _turnAction.enabled) ? Mathf.Clamp(_turnAction.ReadValue<float>(), -1f, 1f) : 0f;
        float drive = (_driveAction != null && _driveAction.enabled) ? _driveAction.ReadValue<float>() : 0f;
        bool forward = usable && drive > 0.01f;
        bool reverse = usable && drive < -0.01f;
        bool boostHeld = usable && _thrustAction != null && _thrustAction.enabled && _thrustAction.IsPressed();

        // New plane (spawn/respawn) or scheme switch: start from where the nose points now.
        if (_kbHeadingPlane != plane)
        {
            _kbHeadingPlane = plane;
            Vector2 up = plane.transform.up;
            _kbHeadingDeg = Mathf.Atan2(up.y, up.x) * Mathf.Rad2Deg;
        }

        // S: turn around once per press, then drift along the new heading while held.
        if (reverse && !_kbReverseHeld)
            _kbHeadingDeg = Mathf.Repeat(_kbHeadingDeg + 180f, 360f);
        _kbReverseHeld = reverse;

        if (usable && Mathf.Abs(_kbTurnAxis) > 0.01f)
            _kbHeadingDeg = Mathf.Repeat(_kbHeadingDeg - _kbTurnAxis * keyboardTurnRateDeg * dt, 360f); // D = clockwise

        float rad = _kbHeadingDeg * Mathf.Deg2Rad;
        Vector2 heading = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        plane.SetHeadingOverride(heading);

        _virtualStick = (forward || reverse || boostHeld) ? heading : Vector2.zero;
    }

    // Mouse scheme: fly toward the cursor as drawn on screen (Camera.main, orthographic in
    // GeneratedTrack — also correct during CameraZoomDirector spotlights).
    //
    // Arriving: the input goes to zero inside an arrive radius that grows with speed, so a fast
    // (boosting) ship lets go before it reaches the cursor instead of overshooting it. It then
    // stays "arrived" until the cursor is more than mouseRearmMultiplier × that radius away. That
    // hysteresis stops the 180° flip-flop a fast ship used to do around a parked cursor (overshoot,
    // cursor now behind, reverse, overshoot again...).
    private void SteerTowardCursor()
    {
        var mouse = PairedMouse;
        var cam = Camera.main;
        if (mouse == null || cam == null) { _virtualStick = Vector2.zero; return; }

        Vector3 screen = mouse.position.ReadValue();
        screen.z = Mathf.Abs(cam.transform.position.z - plane.transform.position.z);
        Vector2 world = cam.ScreenToWorldPoint(screen);
        Vector2 to = world - (Vector2)plane.transform.position;

        float speed = plane.rb != null ? plane.rb.linearVelocity.magnitude : 0f;
        float arrive = Mathf.Max(mouseArriveRadius, speed * mouseArriveSpeedScale);
        float dist = to.magnitude;

        if (_mouseArrived) _mouseArrived = dist <= arrive * mouseRearmMultiplier;
        else _mouseArrived = dist <= arrive;

        _virtualStick = _mouseArrived ? Vector2.zero : to / Mathf.Max(dist, 0.0001f);
    }

    private void Update()
    {
        if (plane == null) return;

        // Keyboard/mouse boost (Shift or left click). Polled with IsPressed so the Thrust action's
        // gamepad-oriented Hold interaction doesn't delay or swallow key presses. The action only
        // sees this player's paired devices under its scheme's binding mask.
        if (IsKbmPlayer && _thrustAction != null)
        {
            bool held = _thrustAction.enabled && _thrustAction.IsPressed()
                        && !_inputLocked && !ModalMenu.ShouldSuppressInput;
            // Mouse: left click. Keyboard: Shift. Both boost immediately.
            bool boost = held;

            if (boost && !_kbmThrustHeld) plane.TurnOnBoost(1f);
            else if (!boost && _kbmThrustHeld) plane.TurnOffBoost();
            _kbmThrustHeld = boost;
        }
    }

    public void OnThrust(InputValue value)
    {
        if (IsKbmPlayer) return; // keyboard/mouse boost is polled in Update
        float v = value.Get<float>();
        if (v > 0 && ModalMenu.ShouldSuppressInput) return;
        if (v > 0) plane?.TurnOnBoost(v);
        else plane?.TurnOffBoost();
        if (GameFlowManager.VerboseLogging) Debug.Log($"[VEHICLE] Thrusting stopped at {v}");

    }

    // PlayerInput (SendMessages) callbacks for actions polled elsewhere; kept so the messages
    // have a receiver.
    public void OnMouseMove(InputValue value) { }
    public void OnTurn(InputValue value) { }
    public void OnDrive(InputValue value) { }

    public void OnQuit(InputValue value)
    {
        // In the "Play" action map, Quit shares its binding with Choose (both on
        // <Gamepad>/buttonSouth, and Quit also on the generic */{Submit} composite that a UI
        // Button click routes through) — so a single press/click fires this in parallel with
        // HandleConfirm. Guard it the same way so it doesn't quit out from under the Living
        // Score publish-consent prompt on the same press that answers it.
        if (LivingScorePanel.ShouldSuppressConfirm) return;
        if (ModalMenu.ShouldSuppressInput) return;

        // Competitive match end: MatchResultsPanel handles "any button -> main menu" itself.
        if (GameFlowManager.Instance.MatchEnded) return;

        if (GameFlowManager.Instance.CurrentState == GameState.GameOver)
        {
            GameFlowManager.Instance.QuitToSelection();
            Destroy(gameObject);
        }
    }

    // A confirmed player can flip co-op/competitive with the Selection Next/Previous actions (D-pad left/right) while 2+ players are joined.
    private bool TryCycleGameMode(int dir)
    {
        var gfm = GameFlowManager.Instance;
        var session = gfm != null ? gfm.SessionState : null;
        if (!_isReady || session == null || session.CurrentState != GameState.Selection || session.Players.Count < 2 || session.ModeLocked)
            return false;

        session.CycleMode(dir);
        _sfxSource?.PlayOneShot(clickFx);
        return true;
    }

    public void OnNext(InputValue value)
    {
        if (!value.isPressed) return;
        if (TryCycleGameMode(+1)) return;
        if (_isReady) return; // confirmed: no ship/mode navigation (or nav sfx) while the tutorial runs
        _sfxSource?.PlayOneShot(clickFx);

        if (_selection != null)
        {
            _selection.Navigate(+1);
            NotifySelectionNavigatedOnce();
        }

        OnNavigateNext?.Invoke();
    }

    public void OnPrevious(InputValue value)
    {
        if (!value.isPressed) return;
        if (TryCycleGameMode(-1)) return;
        if (_isReady) return; // confirmed: no ship/mode navigation (or nav sfx) while the tutorial runs
        _sfxSource?.PlayOneShot(clickFx);

        if (_selection != null)
        {
            _selection.Navigate(-1);
            NotifySelectionNavigatedOnce();
        }

        OnNavigatePrevious?.Invoke();
    }
}
