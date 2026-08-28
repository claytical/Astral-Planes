
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
    private InputAction _leaveAction;
    private InputAction _playLeaveAction;
    private InputAction _releaseAction;

    // Stored so OnDestroy can unsubscribe them — without this, a stale lambda can still
    // fire (e.g. off a shared/global actions asset) after this object is destroyed,
    // throwing MissingReferenceException when it touches Unity APIs like StartCoroutine.
    private System.Action<InputAction.CallbackContext> _onConfirmStarted;
    private System.Action<InputAction.CallbackContext> _onConfirmCanceled;
    private System.Action<InputAction.CallbackContext> _onLeaveStarted;
    private System.Action<InputAction.CallbackContext> _onLeaveCanceled;
    private System.Action<InputAction.CallbackContext> _onPlayLeaveStarted;
    private System.Action<InputAction.CallbackContext> _onPlayLeaveCanceled;
    private System.Action<InputAction.CallbackContext> _onReleasePerformed;
    private System.Action<InputAction.CallbackContext> _onReleaseCanceled;
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
        _onConfirmStarted = ctx =>
        {
            if (!_confirmEnabled) return;
            HandleConfirm();
        };
        _confirmAction.started += _onConfirmStarted;

        _onConfirmCanceled = ctx =>
        {
            _confirmEnabled = true;
        };
        _confirmAction.canceled += _onConfirmCanceled;
        // Arm confirm only if no bound button is currently held.
        // If the join button is still down, we wait for its release (canceled above).
        // If it was already released before we got here, the first real press will confirm.
        if (_confirmAction.ReadValue<float>() <= 0f)
            _confirmEnabled = true;

        // "Leave" exists in both Selection and Play maps (different meanings — see
        // LocalPlayer.LeaveFlow.cs) — qualify both lookups so each resolves to its own map's
        // action instance rather than an ambiguous bare-name match.
        _leaveAction = _playerInput.actions["Selection/Leave"];
        _onLeaveStarted = ctx => HandleLeavePressed();
        _leaveAction.started += _onLeaveStarted;
        _onLeaveCanceled = ctx => HandleLeaveReleased();
        _leaveAction.canceled += _onLeaveCanceled;

        _playLeaveAction = _playerInput.actions["Play/Leave"];
        _onPlayLeaveStarted = ctx => HandlePlayLeavePressed();
        _playLeaveAction.started += _onPlayLeaveStarted;
        _onPlayLeaveCanceled = ctx => HandlePlayLeaveReleased();
        _playLeaveAction.canceled += _onPlayLeaveCanceled;

        // Optional: manual note release action
        _releaseAction = _playerInput.actions.FindAction(releaseActionName, throwIfNotFound: false);
        if (_releaseAction != null)
        {
            _onReleasePerformed = ctx =>
            {
                if (plane != null)
                {
                    plane.TryReleaseQueuedNote();
                    plane.SetReleaseButtonHeld(true);
                }
            };
            _releaseAction.performed += _onReleasePerformed;
            _onReleaseCanceled = ctx =>
            {
                if (plane != null)
                    plane.SetReleaseButtonHeld(false);
            };
            _releaseAction.canceled += _onReleaseCanceled;
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

    private void OnDestroy()
    {
        if (_confirmAction != null)
        {
            _confirmAction.started -= _onConfirmStarted;
            _confirmAction.canceled -= _onConfirmCanceled;
        }
        if (_leaveAction != null)
        {
            _leaveAction.started -= _onLeaveStarted;
            _leaveAction.canceled -= _onLeaveCanceled;
        }
        if (_playLeaveAction != null)
        {
            _playLeaveAction.started -= _onPlayLeaveStarted;
            _playLeaveAction.canceled -= _onPlayLeaveCanceled;
        }
        if (_releaseAction != null)
        {
            _releaseAction.performed -= _onReleasePerformed;
            _releaseAction.canceled -= _onReleaseCanceled;
        }
    }
}
