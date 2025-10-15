
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
    /*
    public void Launch(DrumTrack drums)
    {
        GameObject statsUI = Instantiate(playerStatsUI, GameFlowManager.Instance.PlayerStatsGrid);
        _ui = statsUI.GetComponent<PlayerStats>();
        GameObject vehicle = Instantiate(playerVehicle, transform);
        plane = vehicle.GetComponent<Vehicle>();
        plane.SetDrumTrack(drums);
        var hd = FindAnyObjectByType<HarmonyDirector>();
        if (hd != null)
        {
            plane.SetHarmony(hd);                 // new helper on Vehicle
            hd.Initialize(drums, GameFlowManager.Instance.controller,
                FindAnyObjectByType<ChordChangeArpeggiator>());
        }

        _playerStats = GetComponent<PlayerStatsTracking>();

        if (plane != null)
        {
            plane.playerStats = _playerStats;
            plane.playerStatsUI = _ui;
            plane.SyncEnergyUI();
            plane.GetComponent<SpriteRenderer>().color = _color;
            SetStats();
            _ui.SetColor(_color);
            Debug.Log($"Switching action map to play.");
//            plane.Fly();
            _playerInput.SwitchCurrentActionMap("Play");
        }
    }
    */
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
                GameFlowManager.Instance.harmony &&                       // HarmonyDirector bound
                GameFlowManager.Instance.arp                              // ChordChangeArpeggiator bound
        );

        var gfm = GameFlowManager.Instance;

        // --- UI: player stats card under the grid
        var grid = gfm.PlayerStatsGrid;
        var statsUI = Instantiate(playerStatsUI, grid);
        _ui = statsUI.GetComponent<PlayerStats>();

        // --- Vehicle
        var vehicleGO = Instantiate(playerVehicle, transform);
        plane = vehicleGO.GetComponent<Vehicle>();

        // Use authoritative references from GameFlowManager
        plane.SetDrumTrack(gfm.activeDrumTrack);
        plane.SetHarmony(gfm.harmony);                 // Vehicle keeps a ref; DO NOT re-initialize harmony here

        // Player stats plumbing
        _playerStats = GetComponent<PlayerStatsTracking>();
        if (plane)
        {
            plane.playerStats   = _playerStats;
            plane.playerStatsUI = _ui;
            plane.SyncEnergyUI();

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
    void FixedUpdate()
    {
        
        if (plane != null && _moveInput.sqrMagnitude > 0.01f)
        {
            plane.Move(_moveInput);
            if (_decelerate)
            {
                _moveInput = Vector2.zero;
            }
        }
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
    }
    public void OnMove(InputValue value)
    {
        _moveInput = value.Get<Vector2>().normalized * friction;
    }
    public void OnMouseMove(InputValue value)
    {
        Debug.Log($"OnMouseMove: {value}");
        _moveInput = value.Get<Vector2>().normalized * friction;
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
            if (drumTrack != null && GameFlowManager.Instance.phaseTransitionManager.currentPhase == MusicalPhase.Wildcard)
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
