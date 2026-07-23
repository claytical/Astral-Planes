
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

public partial class LocalPlayer : MonoBehaviour
{
    [SerializeField] private GameObject playerVehicle;
    public Vehicle plane;

    private bool _confirmEnabled, _launchStarted, _launched, _suppressChoose = true;

    private PlayerSelect _selection;
    private PlayerStats _ui;
    private PlayerInput _playerInput;
    private InputAction _confirmAction;
    private InputAction _releaseAction;
    [Header("Gameplay Input")]
    [SerializeField] private string releaseActionName = "ReleaseNote";
    private InputAction _moveAction;

    private int _dustKeepClearOwnerId;
    private bool _dustKeepClearActive;

    // Scene-agnostic navigation delegates.
    // Subscribe from any scene-level UI manager (e.g. PhaseLibraryCarousel).
    public System.Action OnNavigateNext;
    public System.Action OnNavigatePrevious;
    public System.Action OnChooseConfirm;

    private void Awake()
    {
        _playerInput = GetComponent<PlayerInput>();
        if (_playerInput == null) return;

        if (GameFlowManager.VerboseLogging)
            foreach (var dev in _playerInput.devices)
                Debug.Log($"[LocalPlayer.Awake] {dev.name} | product={dev.description.product} | interface={dev.description.interfaceName} | id={dev.deviceId}");
    }

    IEnumerator Start()
    {
        yield return null; // wait one frame so input system "settles"
        _suppressChoose = false;
        DontDestroyOnLoad(this);
        GameFlowManager.Instance.RegisterPlayer(this);

        _moveAction = _playerInput.actions["Move"]; // must match your action name

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
}
