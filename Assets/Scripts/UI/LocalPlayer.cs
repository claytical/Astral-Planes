
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
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
    private PlayerInput playerInput;
    private Color color;
    private bool isReady = false;
    private Vector2 moveInput;
    private bool suppressChoose = true;
    private bool confirmEnabled = false;
    private InputAction confirmAction;
    public float friction = 0.5f;
    public bool IsReady
    {
        get => isReady;
        set => isReady = value;
    }

    IEnumerator Start()
    {
        yield return null; // wait one frame so input system "settles"
        suppressChoose = false;
        DontDestroyOnLoad(this);
        GameFlowManager.Instance.RegisterPlayer(this);
        playerInput = GetComponent<PlayerInput>();
        CreatePlayerSelect();
        playerInput.SwitchCurrentActionMap("Selection");
        confirmAction = playerInput.actions["Choose"]; // use your actual action name
        confirmAction.started += ctx =>
        {
            if (!confirmEnabled)
            {
                // Wait for release before enabling
                return;
            }

            HandleConfirm();
        };

        confirmAction.canceled += ctx =>
        {
            // Button released — allow confirm on next press
            confirmEnabled = true;
        };

    }
    private void HandleConfirm()
    {
        if (isReady) return;

        switch (GameFlowManager.Instance.CurrentState)
        {
            case GameState.Selection:
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

    public void CreatePlayerSelect()
    {
        GameObject ps = Instantiate(playerSelect);
        selection = ps.GetComponent<PlayerSelect>();
        StartRumble(.1f,1, .5f);
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
        plane.SetDrumTrack(drums);
        playerStats = GetComponent<PlayerStatsTracking>();

        if (plane != null)
        {
            plane.playerStats = playerStats;
            plane.playerStatsUI = ui;
            plane.SyncEnergyUI();
            plane.GetComponent<SpriteRenderer>().color = color;
            SetStats();
            ui.SetColor(color);
            Debug.Log($"Switching action map to play.");
//            plane.Fly();
            playerInput.SwitchCurrentActionMap("Play");
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

        GetComponent<PlayerInput>().SwitchCurrentActionMap("Selection");
        Destroy(gameObject);
        GameFlowManager.Instance.QuitToSelection();
    }

    // Input System Actions
    void Update()
    {
    }

    void FixedUpdate()
    {
        
        if (plane != null && moveInput.sqrMagnitude > 0.01f)
        {
            plane.Move(moveInput);
        }
    }
    public void OnMove(InputValue value)
    {
        moveInput = value.Get<Vector2>().normalized * friction;
    }

    public void OnQuit(InputValue value)
    {
        if (GameFlowManager.Instance.CurrentState == GameState.GameOver)
        {
            GameFlowManager.Instance.QuitToSelection();
            Destroy(gameObject);
        }
    }

    public void OnThrust(InputValue value)
    {
        float v = value.Get<float>();
        if (v > 0) plane?.TurnOnBoost(v);
        else plane?.TurnOffBoost();
    }


    public void OnNextVehicle(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (selection != null)
        {
            selection.NextVehicle();
            
        }
    }

    public void OnPreviousVehicle(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (selection != null)
        {
            selection.PreviousVehicle();
        }
    }

    public void OnNextColor(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (selection != null)
        {

            selection.NextColor();
        }
    }

    public void OnPreviousColor(InputValue value)
    {
        if (!value.isPressed) return;
        GetComponent<AudioSource>().PlayOneShot(clickFx);
        if (selection != null)
        {
            selection.PreviousColor();
        }
    }

    public void StartRumble(float lowFreq, float highFreq, float duration)
    {
//        if (GetComponent<PlayerInput>().devices[0] is Gamepad pad)
//            GameFlowManager.Instance.TriggerRumbleForAll(lowFreq, highFreq, duration);
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
