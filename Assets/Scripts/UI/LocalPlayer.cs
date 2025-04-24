
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class LocalPlayer : MonoBehaviour
{
    public GameObject playerSelect;
    public GameObject playerVehicle;
    public GameObject playerStatsUI;
    public AudioClip clickFx;
    public AudioClip confirmFx;

    public Vehicle plane;
    public int astralPlaneIndex = 0;
    private PlayerSelect selection;
    private PlayerStatsTracking playerStats;
    private PlayerStats ui;
    private Color color;
    private bool isReady = false;
    private Vector2 moveInput;
    public bool IsReady => isReady;
    
    void Start()
    {
        DontDestroyOnLoad(this);
        GameFlowManager.Instance.RegisterPlayer(this);
        CreatePlayerSelect();
    }

    public void CreatePlayerSelect()
    {
        GameObject ps = Instantiate(playerSelect);
        selection = ps.GetComponent<PlayerSelect>();
    }

    public void SetStats()
    {
        ui?.SetStats(plane);
    }

    private void SetColor()
    {
        color = selection.planeIcon.color;
    }

    public void Launch(DrumTrack drums)
    {
        GameObject statsUI = Instantiate(playerStatsUI, GameFlowManager.Instance.GetUIParent());
        ui = statsUI.GetComponent<PlayerStats>();
        GameObject vehicle = Instantiate(playerVehicle, transform);
        plane = vehicle.GetComponent<Vehicle>();
        playerStats = GetComponent<PlayerStatsTracking>();

        if (plane != null)
        {
            plane.playerStats = playerStats;
            plane.playerStatsUI = ui;
            plane.SyncEnergyUI();
            plane.GetComponent<SpriteRenderer>().color = color;
            SetStats();
            ui.SetColor(color);
            
//            plane.Fly();
            GetComponent<PlayerInput>().SwitchCurrentActionMap("Play");
        }
    }

    public float GetVehicleEnergy()
    {
        return plane?.energyLevel ?? 0f;
    }

    public string GetSelectedShipName()
    {
        return selection?.GetCurrentShipName();
    }

    private void Restart()
    {
        isReady = false;

        if (selection != null)
        {
            Destroy(selection.gameObject);
        }

        GetComponent<PlayerInput>().SwitchCurrentActionMap("Start");
        Destroy(gameObject);
        GameFlowManager.Instance.QuitToSelection();
    }

    // Input System Actions

    void FixedUpdate()
    {
        if (plane != null && moveInput.sqrMagnitude > 0.01f)
        {
            plane.Move(moveInput);
        }
    }
    public void OnMove(InputValue value)
    {
        moveInput = value.Get<Vector2>().normalized;
    }

    public void OnQuit(InputValue value)
    {
        Destroy(gameObject);
        GameFlowManager.Instance.QuitToSelection();
    }

    public void OnThrust(InputValue value)
    {
        float v = value.Get<float>();
        if (v > 0) plane?.TurnOnBoost(v);
        else plane?.TurnOffBoost();
    }

    public void OnChoose(InputValue value)
    {
        if (!value.isPressed) return;

        switch (GameFlowManager.Instance.CurrentState)
        {
            case GameState.Begin:
                GameFlowManager.Instance.JoinGame();
                GameFlowManager.Instance.CurrentState = GameState.Selection;
                break;
            case GameState.Selection:
                if (isReady) return; // 🛑 already confirmed

                GetComponent<AudioSource>().PlayOneShot(confirmFx);
                playerVehicle = selection.GetChosenPlane();
                selection.Confirm();
                SetColor();
                isReady = true;
                Debug.Log($"Player {name} is ready");
                GameFlowManager.Instance.CheckAllPlayersReady();
                break;

            case GameState.GameOver:
                GetComponent<AudioSource>().PlayOneShot(confirmFx);
                Restart();
                break;
        }
    }

    public void OnNextVehicle(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        selection.NextVehicle();
    }

    public void OnPreviousVehicle(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        selection.PreviousVehicle();
    }

    public void OnNextColor(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        selection.NextColor();
    }

    public void OnPreviousColor(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        selection.PreviousColor();
    }

    public void StartRumble(float lowFreq, float highFreq, float duration)
    {
        Gamepad pad = GetComponent<PlayerInput>().devices[0] as Gamepad;
        if (pad != null)
            GameFlowManager.Instance.TriggerRumbleForAll(lowFreq, highFreq, duration);
    }

    // Wildcard Glitch Toggles
    public void OnNorth(InputValue value) => ToggleGlitch(0);
    public void OnEast(InputValue value) => ToggleGlitch(1);
    public void OnWest(InputValue value) => ToggleGlitch(2);
    public void OnSouth(InputValue value) => ToggleGlitch(3);

    private void ToggleGlitch(int index)
    {
        if (GameFlowManager.Instance.ReadyToPlay() && GameFlowManager.Instance.CurrentState == GameState.Playing)
        {
            var drumTrack = FindAnyObjectByType<DrumTrack>();
            if (drumTrack != null && drumTrack.currentPhase == MusicalPhase.Wildcard)
            {
                GameFlowManager.Instance.glitch.ToggleEffect(index);
            }
        }
    }
    
}
