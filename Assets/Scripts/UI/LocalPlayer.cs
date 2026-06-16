
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class LocalPlayer : MonoBehaviour
{
    public GameObject playerSelect;
    [SerializeField] private GameObject playerVehicle;
    public Vehicle plane;
    [SerializeField] private GameObject playerStatsUI;
    public AudioClip clickFx;
    public AudioClip confirmFx;
    public float friction = 0.5f;
    
    private Color _color;
    private bool _isReady, _confirmEnabled, _launchStarted, _launched, _suppressChoose = true;
    private PlayerSelect _selection;
    private PlayerStatsTracking _playerStats;
    private PlayerStats _ui;
    private PlayerInput _playerInput;
    private InputAction _confirmAction;
    private InputAction _releaseAction;
    [Header("Gameplay Input")]
    [SerializeField] private string releaseActionName = "ReleaseNote";
    private Vector2 _virtualStick;      // accumulated, clamped to radius 1
    [Header("Mouse/Touchpad Virtual Stick")]
    [SerializeField] private float mouseSensitivity = 0.015f; // px -> stick units per frame
    [SerializeField] private float stickMax = 1.0f;           // clamp radius
    [SerializeField] private float stickDecayPerSec = 2.5f;   // exponential decay rate to 0
    [SerializeField] private float uiBlockRadius = 0f;        // optional UI guard
    [SerializeField] private float mouseDeltaSmoothing = 12f; // higher = smoother (EMA rate per second)
    [SerializeField] private float maxPixelsPerFrame = 50f;   // clamp spikes from touchpad/mouse
    [SerializeField] private float angleSmoothTime = 0.08f;   // seconds to smooth heading changes
    [Header("Keyboard")]
    [Tooltip("Degrees per second the heading rotates toward the held arrow direction.")]
    [SerializeField] private float keyboardTurnRateDeg = 270f;
    [Tooltip("Rate at which stick magnitude ramps from 0 to 1 when an arrow key is held (units/sec).")]
    [SerializeField] private float keyboardAccelRate = 5f;
    [Header("Tutorial UI")]
    [SerializeField] private ControlTutorialHighlight miniTutorialPrefab;
    [SerializeField] private Vector3 miniTutorialScale = new Vector3(0.5f, 0.5f, 1f);

    private ControlTutorialHighlight _miniTutorial;
    private bool _hasNavigatedSelectionOnce;

    private Vector2 _smoothedDelta;
    private float _angleVel; // SmoothDampAngle velocity
    private bool _gamepadDriving; // true while a gamepad stick is (or was last) driving _virtualStick
    private InputAction _moveAction;

    // --- Dust spawn pocket / keep-clear ---
    // Use CosmicDustGenerator's ref-counted vehicle keep-clear to carve the spawn cell
    // before instantiating the vehicle, avoiding initial collider interpenetration.
    [Header("Spawn Pocket")]
    [Tooltip("Carve a dust-free pocket at the spawn cell before placing the vehicle.")]
    public bool carveSpawnPocket = true;

    [Tooltip("Radius (in dust grid cells) for the spawn pocket. 0 clears only the spawn cell.")]
    public int spawnPocketRadiusCells = 0;

    [Tooltip("Fade duration for spawn-pocket dust clearing.")]
    public float spawnPocketFadeSeconds = 0.02f;

    private int _dustKeepClearOwnerId;
    private Vector2Int _dustKeepClearCell;
    private bool _dustKeepClearActive;
    // Scene-agnostic navigation delegates.
    // Subscribe from any scene-level UI manager (e.g. PhaseLibraryCarousel).
    public System.Action OnNavigateNext;
    public System.Action OnNavigatePrevious;
    public System.Action OnChooseConfirm;

    public bool IsReady
    {
        get => _isReady;
        set => _isReady = value;
    }

    public void CreatePlayerSelect()
    {
        // If we already had a selection UI from an earlier TrackSelection visit, kill it.
        if (_selection != null)
        {
            Destroy(_selection.gameObject);
            _selection = null;
        }

        if (_miniTutorial != null)
        {
            Destroy(_miniTutorial.gameObject);
            _miniTutorial = null;
        }

        _hasNavigatedSelectionOnce = false;

        GameObject ps = Instantiate(playerSelect);
        _selection = ps.GetComponent<PlayerSelect>();

        // Spawn mini controller and attach it near the player’s selection UI (exactly once)
        if (miniTutorialPrefab != null && _selection != null)
        {
// Choose a very specific anchor transform inside the PlayerSelectShip prefab.
            Transform miniAnchor =
                _selection.tutorialControls
                    ? _selection.tutorialControls.transform
                    : _selection.transform;

            _miniTutorial = Instantiate(miniTutorialPrefab); // instantiate unparented

            if (ControlTutorialDirector.Instance != null)
            {
                ControlTutorialDirector.Instance.RegisterMini(
                    lp: this,
                    mini: _miniTutorial,
                    parentOverride: miniAnchor,
                    localPos: Vector3.zero,
                    localScale: miniTutorialScale,
                    localRot: Quaternion.identity
                );
            }
        }
    }

    public void SetStats()
    {
        _ui?.SetStats(plane);
    }
    private void SetColor()
    {
        _color = _selection.planeIcon.color;
    }

    public void Launch()  // <- no params
    {
        if (_launched || _launchStarted) return;
        _launchStarted = true;
        StartCoroutine(LaunchWhenReady());
    }
    private void NotifySelectionNavigatedOnce()
    {
        if (_hasNavigatedSelectionOnce) return;
        _hasNavigatedSelectionOnce = true;

        if (ControlTutorialDirector.Instance != null)
            ControlTutorialDirector.Instance.Mini_SetConfirmStage(this);
    }

    private IEnumerator LaunchWhenReady()
{
    // Wait for authoritative deps from GameFlowManager
    yield return new WaitUntil(() =>
            GameFlowManager.Instance &&
            GameFlowManager.Instance.PlayerStatsGrid &&               // UI parent exists
            GameFlowManager.Instance.activeDrumTrack &&               // drums ready
            GameFlowManager.Instance.controller &&                    // tracks configured
            GameFlowManager.Instance.harmony                          // HarmonyDirector bound
    );
        
    if (GameFlowManager.VerboseLogging) Debug.Log("[CRASH TEST] Track Ready");
        
    var gfm = GameFlowManager.Instance;

    // --- UI: player stats card under the grid
    var grid = gfm.PlayerStatsGrid;
    var statsUI = Instantiate(playerStatsUI, grid);
    _ui = statsUI.GetComponent<PlayerStats>();
    int w = gfm.spawnGrid.gridWidth;

// pick a row near the bottom; tweak as you want
    int spawnY = 1;
    int spawnX = Random.Range(0, w);
    var spawnCell = new Vector2Int(spawnX, spawnY);
    // 🔹 NEW: place the vehicle at a random grid cell
    var drums = gfm.activeDrumTrack;
    var spawnGrid = gfm.spawnGrid;

    if (drums != null && spawnGrid != null)
    {
        // --- NEW: carve a safe spawn cell BEFORE we place/instantiate the vehicle ---
        if (carveSpawnPocket && gfm.dustGenerator != null)
        {
            _dustKeepClearOwnerId = GetInstanceID();
            _dustKeepClearCell = spawnCell;

            gfm.dustGenerator.SetVehicleKeepClear(
                ownerId: _dustKeepClearOwnerId,
                centerCell: _dustKeepClearCell,
                radiusCells: Mathf.Max(0, spawnPocketRadiusCells),
                forceRemoveExisting: true,
                forceRemoveFadeSeconds: Mathf.Max(0.01f, spawnPocketFadeSeconds)
            );
            _dustKeepClearActive = true;
        }

// Snap LocalPlayer to that grid cell in world space
        Vector3 spawnWorld = drums.GridToWorldPosition(spawnCell);
        transform.position = spawnWorld;

// Optionally mark the cell as occupied so dust never spawns here
        gfm.spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.Node);
        // --- Vehicle
        var vehicleGO = Instantiate(playerVehicle, transform);
        plane = vehicleGO.GetComponent<Vehicle>();
        if (plane != null)
            gfm.RegisterVehicle(plane);
    }

    // Player stats plumbing
    _playerStats = GetComponent<PlayerStatsTracking>();
    if (plane)
    {
        plane.playerStats   = _playerStats;
        plane.playerStatsUI = _ui;
        plane.SyncEnergyUI();
        plane.SetDrumTrack(drums);

        var sr = plane.GetComponent<SpriteRenderer>();
        if (sr) sr.color = _color;
        SetStats();
        _ui.SetColor(_color);
        _playerInput.SwitchCurrentActionMap("Play");
    }

    _launched = true;
}

    public float GetVehicleEnergy()
    {
        return plane?.energyLevel ?? 0f;
    }

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

    public string GetSelectedShipName()
    {
        return _selection?.GetCurrentShipName();
    }

    IEnumerator Start()
    {
        yield return null; // wait one frame so input system "settles"
        _suppressChoose = false;
        DontDestroyOnLoad(this);

        _playerInput = GetComponent<PlayerInput>();

        // Guard against duplicate spawns from Steam virtual/physical device pairs:
        // if another LocalPlayer already owns one of our devices, self-destruct.
        if (HasDuplicateDevice())
        {
            Destroy(gameObject);
            yield break;
        }

        GameFlowManager.Instance.RegisterPlayer(this);

        _moveAction  = _playerInput.actions["Move"]; // must match your action name

        // IMPORTANT: Only create selection UI in TrackSelection.
        // Main scene should NOT spawn PlayerSelect/minis.
        var sceneName = SceneManager.GetActiveScene().name;
        if (sceneName == "TrackSelection")
        {
            CreatePlayerSelect();
        }

        _confirmAction = _playerInput.actions["Choose"]; // use your actual action name
        _confirmAction.started += ctx =>
        {
            if (!_confirmEnabled) return;
            HandleConfirm();
        };

        _confirmAction.canceled += ctx =>
        {
            _confirmEnabled = true;
        };
        // Arm confirm only if no bound button is currently held.
        // If the join button is still down, we wait for its release (canceled above).
        // If it was already released before we got here, the first real press will confirm.
        if (_confirmAction.ReadValue<float>() <= 0f)
            _confirmEnabled = true;

        // Optional: manual note release action
        _releaseAction = _playerInput.actions.FindAction(releaseActionName, throwIfNotFound: false);
        if (_releaseAction != null)
        {
            _releaseAction.performed += ctx =>
            {
                if (plane != null)
                {
                    plane.TryReleaseQueuedNote();
                    plane.SetReleaseButtonHeld(true);
                }
            };
            _releaseAction.canceled += ctx =>
            {
                if (plane != null)
                    plane.SetReleaseButtonHeld(false);
            };
        }
    }

    private bool HasDuplicateDevice()
    {
        if (_playerInput == null || _playerInput.devices.Count == 0) return false;
        foreach (var existing in GameFlowManager.Instance.localPlayers)
        {
            if (existing == null || existing == this) continue;
            var other = existing.GetComponent<PlayerInput>();
            if (other == null) continue;
            foreach (var myDev in _playerInput.devices)
                foreach (var otherDev in other.devices)
                    if (myDev == otherDev)
                        return true;
        }
        return false;
    }

    private void OnDisable()
    {
        var gfm = GameFlowManager.Instance; 
        if (gfm != null && plane != null) gfm.UnregisterVehicle(plane);
        // Release the initial spawn keep-clear so dust can regrow if this player is removed.
        if (!_dustKeepClearActive) return;
        if (gfm != null && gfm.dustGenerator != null)
        {
            gfm.dustGenerator.ReleaseVehicleKeepClear(_dustKeepClearOwnerId);
        }
        _dustKeepClearActive = false;
    }
    void FixedUpdate()
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

    private void HandleConfirm()
    {
        if (GameFlowManager.Instance != null &&
            GameFlowManager.Instance.CurrentState == GameState.Selection &&
            ControlTutorialDirector.Instance != null &&
            ControlTutorialDirector.Instance.IsPrimaryTutorialRunning)
        {
            ControlTutorialDirector.Instance.SkipCurrentTutorialStep();
            return;
        }

        OnChooseConfirm?.Invoke();

        if (_isReady || GameFlowManager.Instance == null) return;

        switch (GameFlowManager.Instance.CurrentState)
        {
            case GameState.Selection:
                GetComponent<AudioSource>().PlayOneShot(confirmFx);
                playerVehicle = _selection.GetChosenPlane();
                _selection.Confirm();
                SetColor();
                _isReady = true;
                IsReady = true;
                if (ControlTutorialDirector.Instance != null)
                    ControlTutorialDirector.Instance.Mini_Clear(this);
                GameFlowManager.Instance.CheckAllPlayersReady();
                break;

            case GameState.GameOver:
                GetComponent<AudioSource>().PlayOneShot(confirmFx);
                Restart();
                break;
        }
    }
    public void ResetReady()
    {
        _isReady = false;
        IsReady = false;
    }
    private void Restart()
    {
        _isReady = false;
        
        if (_selection != null)
        {
            Destroy(_selection.gameObject);
        }

        GetComponent<PlayerInput>().SwitchCurrentActionMap("Selection");
        Destroy(gameObject);
        GameFlowManager.Instance.QuitToSelection();
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
