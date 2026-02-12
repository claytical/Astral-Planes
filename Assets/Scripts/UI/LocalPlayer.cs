
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
    private bool _isReady, _decelerate, _confirmEnabled, _launchStarted, _launched, _suppressChoose = true;
    private Vector2 _moveInput;
    private PlayerSelect _selection;
    private PlayerStatsTracking _playerStats;
    private PlayerStats _ui;
    private PlayerInput _playerInput;
    private InputAction _confirmAction;
    private Vector2 _virtualStick;      // accumulated, clamped to radius 1
    [Header("Mouse/Touchpad Virtual Stick")]
    [SerializeField] private float mouseSensitivity = 0.015f; // px -> stick units per frame
    [SerializeField] private float stickMax = 1.0f;           // clamp radius
    [SerializeField] private float stickDecayPerSec = 2.5f;   // exponential decay rate to 0
    [SerializeField] private float uiBlockRadius = 0f;        // optional UI guard
    [SerializeField] private float mouseDeltaSmoothing = 12f; // higher = smoother (EMA rate per second)
    [SerializeField] private float maxPixelsPerFrame = 50f;   // clamp spikes from touchpad/mouse
    [SerializeField] private float angleSmoothTime = 0.08f;   // seconds to smooth heading changes
    private Vector2 _smoothedDelta;
    private float _angleVel; // SmoothDampAngle velocity
    private bool _usingMouse;
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
    public bool IsReady
    {
        get => _isReady;
        set => _isReady = value;
    }

    public void CreatePlayerSelect()
    {
        GameObject ps = Instantiate(playerSelect);
        _selection = ps.GetComponent<PlayerSelect>();
        StartRumble(.1f,1, .5f);
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
        
    Debug.Log("[CRASH TEST] Track Ready");
        
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

            // Determine phase for regrowth scheduling (keep-clear itself blocks regrow).
            MazeArchetype phaseNow = (gfm.phaseTransitionManager != null)
                ? gfm.phaseTransitionManager.currentPhase
                : MazeArchetype.Establish;

            gfm.dustGenerator.SetVehicleKeepClear(
                ownerId: _dustKeepClearOwnerId,
                centerCell: _dustKeepClearCell,
                radiusCells: Mathf.Max(0, spawnPocketRadiusCells),
                phase: phaseNow,
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

    public string GetSelectedShipName()
    {
        return _selection?.GetCurrentShipName();
    }

    IEnumerator Start()
    {
        yield return null; // wait one frame so input system "settles"
        _suppressChoose = false;
        DontDestroyOnLoad(this);
        GameFlowManager.Instance.RegisterPlayer(this);
        _playerInput = GetComponent<PlayerInput>();
        _moveAction = _playerInput.actions["Move"]; // must match your action name

        CreatePlayerSelect();
        _playerInput.SwitchCurrentActionMap("Selection");
        _confirmAction = _playerInput.actions["Choose"]; // use your actual action name
        _confirmAction.started += ctx =>
        {
            if (!_confirmEnabled)
            {
                // Wait for release before enabling
                return;
            }

            HandleConfirm();
        };

        _confirmAction.canceled += ctx =>
        {
            // Button released — allow confirm on next press
            _confirmEnabled = true;
        };

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

        // Poll current move value every physics tick (works for held keys and held stick)
        Vector2 raw = (_moveAction != null) ? _moveAction.ReadValue<Vector2>() : Vector2.zero;

        // IMPORTANT: don't Normalize() here or you destroy analog magnitude.
        // Clamp to unit circle instead.
        raw = Vector2.ClampMagnitude(raw, 1f);

        // Apply your existing scaling
        _moveInput = raw * friction;

        float dt = Time.fixedDeltaTime;

        // If we have explicit move input (keyboard/gamepad), treat it as the stick target.
        if (_moveInput.sqrMagnitude >= 0.0001f)
        {
            _virtualStick = _moveInput; // snap while held
        }
        else
        {
            // Exponential decay toward center for the virtual stick
            if (_virtualStick.sqrMagnitude > 0f)
            {
                float k = Mathf.Exp(-stickDecayPerSec * dt);
                _virtualStick *= k;
                if (_virtualStick.sqrMagnitude < 0.0001f) _virtualStick = Vector2.zero;
            }
        }

        // Always feed the smoothed stick
        plane.Move(_virtualStick);
    }

    private void HandleConfirm()
    {
        if (_isReady) return;

        switch (GameFlowManager.Instance.CurrentState)
        {
  
            case GameState.Selection:
                GetComponent<AudioSource>().PlayOneShot(confirmFx);
                playerVehicle = _selection.GetChosenPlane();
                _selection.Confirm();
                SetColor();
                _isReady = true;
                Debug.Log($"Player {name} is ready");
                GameFlowManager.Instance.CheckAllPlayersReady();
                break;

            case GameState.GameOver:
                GetComponent<AudioSource>().PlayOneShot(confirmFx);
                Restart();
                break;
        }
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
        Debug.Log($"[VEHICLE] Thrusting stopped at {v}");

    }
    public void OnMove(InputValue value)
    {
        Vector2 v = Vector2.ClampMagnitude(value.Get<Vector2>(), 1f);
        _moveInput = v * friction;
        if (_moveInput.sqrMagnitude > 0f) _usingMouse = false;
    }

    public void OnMouseMove(InputValue value)
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        Vector2 raw = value.Get<Vector2>();        // pixels since last frame
        if (raw.sqrMagnitude == 0f) return;

        _usingMouse = true;

        // 1) clamp per-frame spikes
        raw = Vector2.ClampMagnitude(raw, maxPixelsPerFrame);

        // 2) exponential moving average (frame-rate aware)
        float a = 1f - Mathf.Exp(-mouseDeltaSmoothing * Time.unscaledDeltaTime);
        _smoothedDelta = Vector2.Lerp(_smoothedDelta, raw, a);

        // Convert to "stick units per second" and integrate per frame
        Vector2 deltaStick = _smoothedDelta * mouseSensitivity * friction * Time.unscaledDeltaTime * 60f;

        // Proposed stick (pre-clamp)
        Vector2 proposed = _virtualStick + deltaStick;

        // 3) optional angle smoothing to prevent twitchy heading changes
        if (_virtualStick.sqrMagnitude > 0.000001f && proposed.sqrMagnitude > 0.000001f && angleSmoothTime > 0f)
        {
            float curAng = Mathf.Atan2(_virtualStick.y, _virtualStick.x) * Mathf.Rad2Deg;
            float tgtAng = Mathf.Atan2(proposed.y, proposed.x) * Mathf.Rad2Deg;
            float smAng  = Mathf.SmoothDampAngle(curAng, tgtAng, ref _angleVel, angleSmoothTime);
            float mag    = proposed.magnitude;
            proposed = new Vector2(Mathf.Cos(smAng * Mathf.Deg2Rad), Mathf.Sin(smAng * Mathf.Deg2Rad)) * mag;
        }

        // Final clamp to stick radius
        _virtualStick = (proposed.sqrMagnitude > stickMax * stickMax)
            ? proposed.normalized * stickMax
            : proposed;
    }

    public void OnQuit(InputValue value)
    {
        if (GameFlowManager.Instance.CurrentState == GameState.GameOver)
        {
            GameFlowManager.Instance.QuitToSelection();
            Destroy(gameObject);
        }
    }

    
    public void OnNextVehicle(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (_selection != null)
        {
            _selection.NextVehicle();
            
        }
    }
    public void OnPreviousVehicle(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (_selection != null)
        {
            _selection.PreviousVehicle();
        }
    }
    public void OnNextColor(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (_selection != null)
        {

            _selection.NextColor();
        }
    }
    public void OnPreviousColor(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (_selection != null)
        {
            _selection.PreviousColor();
        }
    }

    private void ToggleGlitch(int index)
    {
        if (GameFlowManager.Instance.ReadyToPlay() && GameFlowManager.Instance.CurrentState == GameState.Playing)
        {
            var drumTrack = FindAnyObjectByType<DrumTrack>();
            if (drumTrack != null && GameFlowManager.Instance.phaseTransitionManager.currentPhase == MazeArchetype.Wildcard)
            {
                GameFlowManager.Instance.glitch.ToggleEffect(index);
            }
        }
    }
    private void StartRumble(float lowFreq, float highFreq, float duration)
    {
//        if (GetComponent<PlayerInput>().devices[0] is Gamepad pad)
//            GameFlowManager.Instance.TriggerRumbleForAll(lowFreq, highFreq, duration);
    }
    public void OnNorth(InputValue value)
    {
        _moveInput = Vector2.up * friction;
        if (!value.isPressed)
        {
            _decelerate = true;
        }
    }
    public void OnEast(InputValue value)
    {
        _moveInput = Vector2.right * friction;
        if (!value.isPressed)
        {
            _decelerate = true;
        }

    }
    public void OnWest(InputValue value)
    {
        _moveInput = Vector2.left * friction;
        if (!value.isPressed)
        {
            _decelerate = true;
        }

    }
    public void OnSouth(InputValue value)
    {
        _moveInput = Vector2.down * friction;
        if (!value.isPressed)
        {
            _decelerate = true;
        }
    }
    
}
