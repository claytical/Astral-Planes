// Native Steam Input API driver. When Steam is running, this is the gameplay input authority for
// every gamepad: each controller Steam reports gets one SteamInputGamepad (a semantic Unity
// Gamepad, see SteamInputGamepad.cs) that is fed from ISteamInput action data just before every
// Input System update. Native Unity gamepads (XInput, DualShock/DualSense HID, Switch Pro HID,
// Steam's own virtual pads, the Steam Deck built-in) are disabled while this is active, so each
// physical controller shows up exactly once and the existing PlayerInput / <Gamepad> bindings /
// raw Gamepad polls all keep working unchanged.
//
// If Steam isn't running, SteamInput.Init fails, or no Steam controller has a bound "Choose"
// action (e.g. the action manifest/configs aren't set up yet), nothing is disabled and the game
// falls back to Unity's native devices (see PadSemantics.cs for the Switch A/B swap there).
//
// Mirrors SteamManager.cs's init-then-poll shape, lives on the SteamManager prefab, and every
// Steam call is fail-silent like SteamStatsManager.cs. The public API uses only plain C# / Unity
// types so callers compile identically whether or not DISABLESTEAMWORKS is set.
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.XInput;
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
using UnityEngine.InputSystem.Switch;
#endif
#if !DISABLESTEAMWORKS
using System.Collections.Generic;
using System.IO;
using UnityEngine.InputSystem.LowLevel;
using Steamworks;
#endif

public enum ControllerBrand { Unknown, Generic, Xbox, PlayStation, Switch, SteamController, SteamDeck }

[DisallowMultipleComponent]
public class SteamInputManager : MonoBehaviour
{
    public static SteamInputManager Instance { get; private set; }

    [Tooltip("Path to the in-game actions manifest, relative to StreamingAssets.")]
    [SerializeField] private string manifestRelativePath = "steam_input/in_game_actions.vdf";

    [Tooltip("Steam Input action set name defined in the manifest.")]
    [SerializeField] private string actionSetName = "Game";

    [Tooltip("When off, Steam Input is only used for controller brand + glyphs and Unity's native " +
             "gamepads drive gameplay. Kill switch if the Steam path misbehaves.")]
    [SerializeField] private bool driveGameplay = true;

    // Action names in the manifest (in_game_actions.vdf). Also used as glyph lookup keys.
    public const string ActionMove = "Move";
    public const string ActionThrust = "Thrust";
    public const string ActionChoose = "Choose";
    public const string ActionBack = "Back";
    public const string ActionWest = "West";
    public const string ActionNorth = "North";
    public const string ActionLeftShoulder = "LeftShoulder";
    public const string ActionRightShoulder = "RightShoulder";
    public const string ActionMenu = "Menu";
    public const string ActionStart = "Start";
    public const string ActionMenuUp = "MenuUp";
    public const string ActionMenuDown = "MenuDown";
    public const string ActionMenuLeft = "MenuLeft";
    public const string ActionMenuRight = "MenuRight";

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Update()
    {
        TryBecomeReady();
    }

    /// <summary>
    /// Brand of any Unity gamepad, Steam-driven or native. Used for glyph fallbacks and for
    /// PadSemantics' Switch A/B swap on the non-Steam path.
    /// </summary>
    public static ControllerBrand BrandOf(Gamepad pad)
    {
        if (pad == null) return ControllerBrand.Unknown;
        if (pad is SteamInputGamepad steamPad) return steamPad.Brand;
        if (pad is DualShockGamepad) return ControllerBrand.PlayStation;
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
        if (pad is SwitchProControllerHID) return ControllerBrand.Switch;
#endif
        if (pad is XInputController) return ControllerBrand.Xbox;
        return ControllerBrand.Generic;
    }

#if !DISABLESTEAMWORKS
    /// <summary>Steam Input initialized (brand + glyph queries available).</summary>
    public static bool Initialized => Instance != null && Instance._ready;

    /// <summary>
    /// Steam Input is currently the gameplay authority: native gamepads are disabled and every
    /// controller arrives as a SteamInputGamepad.
    /// </summary>
    public static bool DrivesGameplay => Instance != null && Instance._ready && Instance._nativesDisabled;

    private bool _initAttempted;
    private bool _ready;
    private bool _nativesDisabled;
    private InputActionSetHandle_t _actionSetHandle;

    private struct DigitalBinding
    {
        public string action;
        public GamepadButton button;
        public InputDigitalActionHandle_t handle;
    }

    private DigitalBinding[] _digital;
    private InputAnalogActionHandle_t _moveHandle;
    private InputAnalogActionHandle_t _thrustHandle;
    private readonly Dictionary<string, InputDigitalActionHandle_t> _digitalByName = new();
    private readonly Dictionary<string, InputAnalogActionHandle_t> _analogByName = new();

    private readonly InputHandle_t[] _handleBuffer = new InputHandle_t[Constants.STEAM_INPUT_MAX_COUNT];
    private readonly EInputActionOrigin[] _originBuffer = new EInputActionOrigin[Constants.STEAM_INPUT_MAX_ORIGINS];
    private readonly Dictionary<ulong, SteamInputGamepad> _devices = new();
    private readonly HashSet<ulong> _seenThisFrame = new();
    private readonly HashSet<ulong> _parked = new();
    private readonly List<ulong> _toRemove = new();
    private readonly List<InputDevice> _disabledNatives = new();

    private void TryBecomeReady()
    {
        if (_ready || _initAttempted) return;
        if (!SteamManager.Initialized) return;

        _initAttempted = true;

        try
        {
            string manifestPath = Path.Combine(Application.streamingAssetsPath, manifestRelativePath);
            SteamInput.SetInputActionManifestFilePath(manifestPath);

            // Explicit RunFrame: we call it from InputSystem.onBeforeUpdate so action data is
            // fresh in the same Input System update that consumes it.
            if (!SteamInput.Init(true))
            {
                Debug.LogWarning("[STEAMINPUT] SteamInput.Init failed; using Unity native gamepads.");
                return;
            }

            _actionSetHandle = SteamInput.GetActionSetHandle(actionSetName);
            _moveHandle = GetAnalogHandle(ActionMove);
            _thrustHandle = GetAnalogHandle(ActionThrust);
            _digital = new[]
            {
                Bind(ActionChoose, GamepadButton.South),
                Bind(ActionBack, GamepadButton.East),
                Bind(ActionWest, GamepadButton.West),
                Bind(ActionNorth, GamepadButton.North),
                Bind(ActionLeftShoulder, GamepadButton.LeftShoulder),
                Bind(ActionRightShoulder, GamepadButton.RightShoulder),
                Bind(ActionMenu, GamepadButton.Select),
                Bind(ActionStart, GamepadButton.Start),
                Bind(ActionMenuUp, GamepadButton.DpadUp),
                Bind(ActionMenuDown, GamepadButton.DpadDown),
                Bind(ActionMenuLeft, GamepadButton.DpadLeft),
                Bind(ActionMenuRight, GamepadButton.DpadRight),
            };

            _ready = true;
            InputSystem.onBeforeUpdate += OnBeforeInputUpdate;
            InputSystem.onDeviceChange += OnDeviceChange;

            Debug.Log($"[STEAMINPUT] Ready (actionSet={actionSetName}, driveGameplay={driveGameplay}).");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[STEAMINPUT] Init threw: {e.Message}");
        }
    }

    private DigitalBinding Bind(string action, GamepadButton button)
        => new DigitalBinding { action = action, button = button, handle = GetDigitalHandle(action) };

    private InputDigitalActionHandle_t GetDigitalHandle(string action)
    {
        if (!_digitalByName.TryGetValue(action, out var h))
        {
            h = SteamInput.GetDigitalActionHandle(action);
            _digitalByName[action] = h;
        }
        return h;
    }

    private InputAnalogActionHandle_t GetAnalogHandle(string action)
    {
        if (!_analogByName.TryGetValue(action, out var h))
        {
            h = SteamInput.GetAnalogActionHandle(action);
            _analogByName[action] = h;
        }
        return h;
    }

    private void OnDisable()
    {
        if (Instance != this || !_ready) return;

        InputSystem.onBeforeUpdate -= OnBeforeInputUpdate;
        InputSystem.onDeviceChange -= OnDeviceChange;
        RemoveAllSteamDevices();
        RestoreNatives();
        _ready = false;

        // SteamManager shuts down the SteamAPI in OnDestroy, which runs after this OnDisable.
        try { SteamInput.Shutdown(); }
        catch (System.Exception e) { Debug.LogWarning($"[STEAMINPUT] Shutdown threw: {e.Message}"); }
    }

    // ── Per-frame feed ─────────────────────────────────────────────────────────

    private void OnBeforeInputUpdate()
    {
        if (!_ready) return;

        // Skip editor-only updates (edit-mode / editor window updates).
        if (InputState.currentUpdateType == InputUpdateType.Editor) return;

        try
        {
            // Init(true) means we own RunFrame; glyph/brand queries need it even when Steam
            // isn't driving gameplay.
            SteamInput.RunFrame();
            if (!driveGameplay) return;

            int count = SteamInput.GetConnectedControllers(_handleBuffer);
            _seenThisFrame.Clear();

            bool allBound = count > 0;
            for (int i = 0; i < count; i++)
            {
                var handle = _handleBuffer[i];
                ulong id = handle.m_InputHandle;
                if (id == 0) continue;
                _seenThisFrame.Add(id);

                if (!_devices.TryGetValue(id, out var device))
                {
                    device = AddSteamDevice(handle);
                    if (device == null) { allBound = false; continue; }
                }

                if (!HasBinding(handle, _digital[0].handle)) allBound = false;

                InputSystem.QueueStateEvent(device, ReadState(handle));
            }

            // Disconnected controllers are parked, not removed: neutral state once, and the same
            // device (still paired to its player's PlayerInput) resumes if Steam reports the
            // same handle again. Nothing in the game handles PlayerInput device-lost/regained.
            foreach (var kv in _devices)
            {
                if (_seenThisFrame.Contains(kv.Key)) { _parked.Remove(kv.Key); continue; }
                if (_parked.Add(kv.Key))
                {
                    InputSystem.QueueStateEvent(kv.Value, new GamepadState());
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[STEAMINPUT] Parked disconnected handle={kv.Key}");
                }
            }

            // Steam is the authority only while every connected Steam controller actually has
            // "Choose" bound — otherwise a missing/unpublished config would leave players with
            // no working input at all. With no controllers connected, keep the current state
            // (hot-unplug shouldn't flip natives back on and re-introduce duplicates).
            if (count > 0)
            {
                if (allBound && !_nativesDisabled) DisableNatives();
                else if (!allBound && _nativesDisabled) RestoreNatives();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[STEAMINPUT] Frame update threw: {e.Message}");
        }
    }

    private GamepadState ReadState(InputHandle_t handle)
    {
        var state = new GamepadState();

        var move = SteamInput.GetAnalogActionData(handle, _moveHandle);
        if (move.bActive != 0) state.leftStick = Vector2.ClampMagnitude(new Vector2(move.x, move.y), 1f);

        var thrust = SteamInput.GetAnalogActionData(handle, _thrustHandle);
        if (thrust.bActive != 0) state.rightTrigger = Mathf.Clamp01(thrust.x);

        for (int i = 0; i < _digital.Length; i++)
        {
            var data = SteamInput.GetDigitalActionData(handle, _digital[i].handle);
            if (data.bActive != 0 && data.bState != 0)
                state = state.WithButton(_digital[i].button);
        }

        return state;
    }

    private bool HasBinding(InputHandle_t handle, InputDigitalActionHandle_t action)
        => SteamInput.GetDigitalActionOrigins(handle, _actionSetHandle, action, _originBuffer) > 0;

    // ── Device lifecycle ───────────────────────────────────────────────────────

    private SteamInputGamepad AddSteamDevice(InputHandle_t handle)
    {
        SteamInput.ActivateActionSet(handle, _actionSetHandle);

        var type = SteamInput.GetInputTypeForHandle(handle);
        var device = InputSystem.AddDevice<SteamInputGamepad>($"SteamInput_{ToControllerBrand(type)}");
        device.SteamHandle = handle.m_InputHandle;
        device.Brand = ToControllerBrand(type);
        _devices[handle.m_InputHandle] = device;

        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[STEAMINPUT] Added {device.name} | handle={handle.m_InputHandle} | type={type} | id={device.deviceId}");
        return device;
    }

    private void RemoveSteamDevice(ulong id)
    {
        if (!_devices.TryGetValue(id, out var device)) return;
        _devices.Remove(id);
        _parked.Remove(id);
        if (device != null && device.added) InputSystem.RemoveDevice(device);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[STEAMINPUT] Removed handle={id}");
    }

    private void RemoveAllSteamDevices()
    {
        _toRemove.Clear();
        _toRemove.AddRange(_devices.Keys);
        foreach (var id in _toRemove) RemoveSteamDevice(id);
    }

    // ── Native gamepad suppression ─────────────────────────────────────────────

    private static bool IsNativeGamepad(InputDevice d) => d is Gamepad && d is not SteamInputGamepad;

    private void DisableNatives()
    {
        _nativesDisabled = true;
        foreach (var d in InputSystem.devices)
            if (IsNativeGamepad(d)) DisableNative(d);
        Debug.Log($"[STEAMINPUT] Driving gameplay; disabled {_disabledNatives.Count} native gamepad(s).");
    }

    private void DisableNative(InputDevice d)
    {
        if (!d.enabled) return;
        InputSystem.DisableDevice(d);
        _disabledNatives.Add(d);
        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[STEAMINPUT] Disabled native {d.name} | id={d.deviceId} | product={d.description.product}");
    }

    private void RestoreNatives()
    {
        _nativesDisabled = false;
        foreach (var d in _disabledNatives)
            if (d != null && d.added && !d.enabled) InputSystem.EnableDevice(d);
        if (_disabledNatives.Count > 0)
            Debug.Log($"[STEAMINPUT] Restored {_disabledNatives.Count} native gamepad(s).");
        _disabledNatives.Clear();
    }

    private void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (!_nativesDisabled) return;
        if ((change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected
             || change == InputDeviceChange.Enabled) && IsNativeGamepad(device))
            DisableNative(device);
    }

    // ── Brand + glyph queries ──────────────────────────────────────────────────

    private static ControllerBrand ToControllerBrand(ESteamInputType type)
    {
        switch (type)
        {
            case ESteamInputType.k_ESteamInputType_XBox360Controller:
            case ESteamInputType.k_ESteamInputType_XBoxOneController:
                return ControllerBrand.Xbox;
            case ESteamInputType.k_ESteamInputType_PS3Controller:
            case ESteamInputType.k_ESteamInputType_PS4Controller:
            case ESteamInputType.k_ESteamInputType_PS5Controller:
                return ControllerBrand.PlayStation;
            case ESteamInputType.k_ESteamInputType_SwitchJoyConPair:
            case ESteamInputType.k_ESteamInputType_SwitchJoyConSingle:
            case ESteamInputType.k_ESteamInputType_SwitchProController:
                return ControllerBrand.Switch;
            case ESteamInputType.k_ESteamInputType_SteamController:
                return ControllerBrand.SteamController;
            case ESteamInputType.k_ESteamInputType_SteamDeckController:
                return ControllerBrand.SteamDeck;
            case ESteamInputType.k_ESteamInputType_GenericGamepad:
                return ControllerBrand.Generic;
            default:
                return ControllerBrand.Unknown;
        }
    }

    // Steam handle for a Unity gamepad. Native pads can't be mapped to a specific Steam handle,
    // so they fall back to the first connected controller (correct for single-player).
    private InputHandle_t HandleFor(Gamepad pad)
    {
        if (pad is SteamInputGamepad steamPad) return new InputHandle_t(steamPad.SteamHandle);
        int count = SteamInput.GetConnectedControllers(_handleBuffer);
        return count > 0 ? _handleBuffer[0] : default;
    }

    /// <summary>Brand of the given pad (or the first Steam controller when pad is null).</summary>
    public ControllerBrand GetControllerBrand(Gamepad pad = null)
    {
        if (pad != null && pad is not SteamInputGamepad) return BrandOf(pad);
        if (pad is SteamInputGamepad sp) return sp.Brand;
        if (!_ready) return ControllerBrand.Unknown;

        try
        {
            var handle = HandleFor(null);
            if (handle.m_InputHandle == 0) return ControllerBrand.Unknown;
            return ToControllerBrand(SteamInput.GetInputTypeForHandle(handle));
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[STEAMINPUT] GetControllerBrand threw: {e.Message}");
            return ControllerBrand.Unknown;
        }
    }

    /// <summary>
    /// Absolute path to Steam's PNG glyph for whatever physical button is bound to a digital
    /// action on this pad's controller, or null.
    /// </summary>
    public string GetGlyphPngPath(Gamepad pad, string actionName)
    {
        if (!_ready || string.IsNullOrEmpty(actionName)) return null;

        try
        {
            var handle = HandleFor(pad);
            if (handle.m_InputHandle == 0) return null;

            int count = SteamInput.GetDigitalActionOrigins(handle, _actionSetHandle, GetDigitalHandle(actionName), _originBuffer);
            if (count <= 0) return null;

            string path = SteamInput.GetGlyphPNGForActionOrigin(_originBuffer[0], ESteamInputGlyphSize.k_ESteamInputGlyphSize_Medium, 0);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[STEAMINPUT] GetGlyphPngPath('{actionName}') threw: {e.Message}");
            return null;
        }
    }
#else
    public static bool Initialized => false;
    public static bool DrivesGameplay => false;
    private void TryBecomeReady() { }
    public ControllerBrand GetControllerBrand(Gamepad pad = null) => BrandOf(pad);
    public string GetGlyphPngPath(Gamepad pad, string actionName) => null;
#endif // !DISABLESTEAMWORKS
}
