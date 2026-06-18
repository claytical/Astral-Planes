using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Replaces PlayerInputManager's automatic join detection with explicit per-frame scanning.
///
/// Steam registers each physical controller as multiple Unity InputDevices:
///   (a) Raw HID device  — native button layout (Switch Pro: physical-A = buttonEast,
///                          physical-B = buttonSouth).
///   (b) Steam Virtual Gamepad — Xbox-layout remapping (physical-A = buttonSouth).
///   (c) Steam Deck built-in — mirrors external controller input with a short delay.
///
/// Problems this causes:
///   • Physical-B press fires buttonSouth on the raw HID → phantom join during gameplay.
///   • Steam Deck built-in mirrors the initial A press → second player at scene load.
///
/// Fix:
///   1. ExcludeRawHidDuplicates() at startup: if Steam Virtual gamepads (non-HID interface)
///      exist, permanently exclude all raw HID gamepads. They are always redundant with the
///      virtual devices and have the wrong button layout for the game's action maps.
///   2. Global 500ms join cooldown: after any device joins, all other devices are blocked for
///      GraceSeconds, catching delayed mirrors from the Steam Deck built-in.
///   3. Only buttonSouth and startButton are accepted as join triggers.
///
/// Requires PlayerInputManager.joinBehavior = JoinPlayersManually in the TrackSelection scene.
/// Self-installs via RuntimeInitializeOnLoadMethod — no scene setup needed.
/// </summary>
public class TrackSelectionJoinController : MonoBehaviour
{
    private readonly HashSet<int> _excludedIds = new();
    private float _lastJoinTime = float.MinValue;
    private const float GraceSeconds = 0.5f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RegisterHook()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "TrackSelection") return;
        var go = new GameObject("[JoinController]");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<TrackSelectionJoinController>();
    }

    private void Start()
    {
        foreach (var dev in InputSystem.devices)
            Debug.Log($"[JoinController] Device: {dev.name} | id={dev.deviceId} | " +
                      $"product={dev.description.product} | interface={dev.description.interfaceName}");

        // Exclude any HID gamepads already present.
        foreach (var gp in InputSystem.devices.OfType<Gamepad>())
            TryExcludeHidGamepad(gp);

        // Also catch devices added after Start() — Unity's input system sometimes registers
        // controllers asynchronously, so a second controller's raw HID may appear after the
        // initial scan and would otherwise bypass exclusion.
        InputSystem.onDeviceChange += OnDeviceChange;
    }

    private void OnDestroy()
    {
        InputSystem.onDeviceChange -= OnDeviceChange;
    }

    private void OnDeviceChange(InputDevice dev, InputDeviceChange change)
    {
        if (change == InputDeviceChange.Added && dev is Gamepad gp)
        {
            Debug.Log($"[JoinController] Device added: {gp.name} | id={gp.deviceId} | " +
                      $"product={gp.description.product} | interface={gp.description.interfaceName}");
            TryExcludeHidGamepad(gp);
        }
        else if (change == InputDeviceChange.Removed)
        {
            _excludedIds.Remove(dev.deviceId);
        }
    }

    // Excludes a raw HID gamepad when Steam Virtual (non-HID) gamepads also exist.
    // Raw HID devices use the controller's native button layout (Switch Pro: physical-A = buttonEast,
    // physical-B = buttonSouth) rather than the Xbox remapping the game's action maps expect.
    // They are always redundant with the Steam Virtual entry for the same physical controller.
    private void TryExcludeHidGamepad(Gamepad gp)
    {
        if (gp.description.interfaceName != "HID") return;
        bool hasSteamVirtual = InputSystem.devices.OfType<Gamepad>()
            .Any(g => g.description.interfaceName != "HID");
        if (!hasSteamVirtual) return;

        _excludedIds.Add(gp.deviceId);
        Debug.Log($"[JoinController] Excluded raw HID (Steam Virtual present): " +
                  $"{gp.name} | id={gp.deviceId} | product={gp.description.product}");
    }

    private void Update()
    {
        var pim = PlayerInputManager.instance;
        if (pim == null || !pim.joiningEnabled) return;

        if (GameFlowManager.Instance?.CurrentState != GameState.Selection) return;

        var assignedIds = new HashSet<int>();
        foreach (var lp in FindObjectsOfType<LocalPlayer>())
        {
            var pi = lp.GetComponent<PlayerInput>();
            if (pi == null) continue;
            foreach (var dev in pi.devices)
                assignedIds.Add(dev.deviceId);
        }

        // Only non-HID gamepads (Steam Virtuals, built-in) are candidates.
        // Only buttonSouth and startButton are valid join triggers — buttonEast is excluded
        // because it corresponds to physical-A on raw HID devices (already excluded above) and
        // physical-B on Steam Virtual remapping, neither of which should trigger a join.
        var candidates = InputSystem.devices
            .OfType<Gamepad>()
            .Where(gp => !assignedIds.Contains(gp.deviceId) &&
                         !_excludedIds.Contains(gp.deviceId) &&
                         (gp.buttonSouth.wasPressedThisFrame ||
                          gp.startButton.wasPressedThisFrame))
            .OrderBy(g => g.deviceId)
            .ToList();

        if (candidates.Count == 0) return;

        float now = Time.unscaledTime;

        // Same-frame dedup: only one join per button type per frame.
        // Global cooldown: after any join, block all other devices for GraceSeconds.
        // The cooldown is checked inside the loop so that if multiple candidates pass usedButtons
        // in the same frame, only the first one (lowest deviceId) actually joins.
        var usedButtons = new HashSet<string>();
        foreach (var gp in candidates)
        {
            string btn = null;
            if      (gp.buttonSouth.wasPressedThisFrame && usedButtons.Add("south")) btn = "south";
            else if (gp.startButton.wasPressedThisFrame && usedButtons.Add("start")) btn = "start";

            if (btn == null) continue;

            if (now - _lastJoinTime < GraceSeconds)
            {
                Debug.Log($"[JoinController] Grace block: {gp.name} | id={gp.deviceId} | " +
                          $"product={gp.description.product} | btn={btn} | " +
                          $"{(now - _lastJoinTime) * 1000f:F0}ms since last join");
                continue;
            }

            Debug.Log($"[JoinController] Joining: {gp.name} | id={gp.deviceId} | " +
                      $"product={gp.description.product} | interface={gp.description.interfaceName} | btn={btn}");
            pim.JoinPlayer(pairWithDevices: new InputDevice[] { gp });
            _lastJoinTime = now;
            assignedIds.Add(gp.deviceId);
        }
    }
}
