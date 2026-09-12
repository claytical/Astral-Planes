// Steam Input glyph/device-type oracle. Mirrors SteamManager.cs's init-then-poll shape and
// lives alongside it (co-located on the SteamManager prefab). This class never reads gameplay
// input — Unity's Input System remains the sole authority for that. It only answers two
// questions for UI: "what kind of controller is this?" and "what does Steam's own glyph for
// this action look like?" Every Steam call is fail-silent, matching SteamStatsManager.cs.
//
// Public API deliberately uses only plain C# types (ControllerBrand, string), never raw
// Steamworks types — SteamStatsManager.cs does the same. That keeps callers (e.g.
// ControllerGlyphProvider) compiling identically whether or not DISABLESTEAMWORKS is set.
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using UnityEngine;
#if !DISABLESTEAMWORKS
using System.Collections.Generic;
using System.IO;
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
    [SerializeField] private string actionSetName = "Play";

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

#if !DISABLESTEAMWORKS
    public static bool Initialized => Instance != null && Instance._ready;

    private bool _initAttempted;
    private bool _ready;
    private InputActionSetHandle_t _actionSetHandle;
    private readonly Dictionary<string, InputDigitalActionHandle_t> _digitalHandles = new();

    private void TryBecomeReady()
    {
        if (_ready || _initAttempted) return;
        if (!SteamManager.Initialized) return;

        _initAttempted = true;

        try
        {
            string manifestPath = Path.Combine(Application.streamingAssetsPath, manifestRelativePath);
            SteamInput.SetInputActionManifestFilePath(manifestPath);

            if (!SteamInput.Init(false))
            {
                if (GameFlowManager.VerboseLogging) Debug.LogWarning("[SteamInputManager] SteamInput.Init failed.");
                return;
            }

            _actionSetHandle = SteamInput.GetActionSetHandle(actionSetName);
            _ready = true;

            if (GameFlowManager.VerboseLogging) Debug.Log("[SteamInputManager] Ready.");
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamInputManager] Init threw: {e.Message}");
        }
    }

    private InputHandle_t GetPrimaryControllerHandle()
    {
        var handles = new InputHandle_t[Constants.STEAM_INPUT_MAX_COUNT];
        int count = SteamInput.GetConnectedControllers(handles);
        return count > 0 ? handles[0] : default;
    }

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

    public ControllerBrand GetPrimaryControllerBrand()
    {
        if (!_ready) return ControllerBrand.Unknown;

        try
        {
            var handle = GetPrimaryControllerHandle();
            if (handle.m_InputHandle == 0) return ControllerBrand.Unknown;
            return ToControllerBrand(SteamInput.GetInputTypeForHandle(handle));
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamInputManager] GetPrimaryControllerBrand threw: {e.Message}");
            return ControllerBrand.Unknown;
        }
    }

    public string GetGlyphPngPath(string actionName)
    {
        if (!_ready || string.IsNullOrEmpty(actionName)) return null;

        try
        {
            var handle = GetPrimaryControllerHandle();
            if (handle.m_InputHandle == 0) return null;

            if (!_digitalHandles.TryGetValue(actionName, out var actionHandle))
            {
                actionHandle = SteamInput.GetDigitalActionHandle(actionName);
                _digitalHandles[actionName] = actionHandle;
            }

            var origins = new EInputActionOrigin[Constants.STEAM_INPUT_MAX_ORIGINS];
            int count = SteamInput.GetDigitalActionOrigins(handle, _actionSetHandle, actionHandle, origins);
            if (count <= 0) return null;

            string path = SteamInput.GetGlyphPNGForActionOrigin(origins[0], ESteamInputGlyphSize.k_ESteamInputGlyphSize_Medium, 0);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamInputManager] GetGlyphPngPath('{actionName}') threw: {e.Message}");
            return null;
        }
    }
#else
    public static bool Initialized => false;
    private void TryBecomeReady() { }
    public ControllerBrand GetPrimaryControllerBrand() => ControllerBrand.Unknown;
    public string GetGlyphPngPath(string actionName) => null;
#endif // !DISABLESTEAMWORKS
}
