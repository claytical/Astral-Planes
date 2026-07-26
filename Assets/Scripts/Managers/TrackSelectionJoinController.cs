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
///   3. buttonSouth, buttonEast, and startButton are accepted as join triggers.
///      - With Steam: physical-A maps to buttonSouth (Xbox remapping) → joins on south.
///      - Without Steam (editor / raw HID): physical-A maps to buttonEast → joins on east.
///      Both paths land the player in the same flow; confirm (Choose) is south-only everywhere.
///   4. buttonEast is overloaded once the player is joined (see LocalPlayer.LeaveFlow.cs — it's
///      also "back"). To avoid a tap-join instantly consuming the same press a hold-back needs,
///      East's join is deferred to release (tap = join, sustained hold = back out to the
///      carousel/Main scene). See UpdateHoldBackToCarousel(). South/Start joins stay instant.
///
/// Requires PlayerInputManager.joinBehavior = JoinPlayersManually in the TrackSelection scene.
/// Self-installs via RuntimeInitializeOnLoadMethod — no scene setup needed.
/// </summary>
public class TrackSelectionJoinController : MonoBehaviour
{
    private readonly HashSet<int> _excludedIds = new();
    private float _lastJoinTime = float.MinValue;
    private const float GraceSeconds = 0.5f;

    // Held (not tapped) East, while nobody has joined yet, backs out to the carousel (Main
    // scene) instead of joining. A quick tap still joins immediately, so raw-HID controllers
    // (whose only join button is East, see class doc) are unaffected.
    private int _holdBackDeviceId = -1;
    private float _holdBackStartTime;
    private const float HoldBackSeconds = 1.5f;

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
        if (GameFlowManager.VerboseLogging)
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
            if (GameFlowManager.VerboseLogging)
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
        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[JoinController] Excluded raw HID (Steam Virtual present): " +
                      $"{gp.name} | id={gp.deviceId} | product={gp.description.product}");
    }

    private void Update()
    {
        var pim = PlayerInputManager.instance;
        if (pim == null || !pim.joiningEnabled) return;

        if (GameFlowManager.Instance?.CurrentState != GameState.Selection) return;

        var assignedIds = new HashSet<int>();
        foreach (var lp in FindObjectsByType<LocalPlayer>(FindObjectsSortMode.None))
        {
            var pi = lp.GetComponent<PlayerInput>();
            if (pi == null) continue;
            foreach (var dev in pi.devices)
                assignedIds.Add(dev.deviceId);
        }

        UpdateHoldBackToCarousel(pim, assignedIds);

        // buttonSouth and startButton are instant join triggers. buttonEast is handled by
        // UpdateHoldBackToCarousel above: a quick tap joins (same as before), a sustained hold
        // backs out to the carousel instead — see that method for why East can't also be
        // instant here.
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
                if (GameFlowManager.VerboseLogging)
                    Debug.Log($"[JoinController] Grace block: {gp.name} | id={gp.deviceId} | " +
                              $"product={gp.description.product} | btn={btn} | " +
                              $"{(now - _lastJoinTime) * 1000f:F0}ms since last join");
                continue;
            }

            if (GameFlowManager.VerboseLogging)
                Debug.Log($"[JoinController] Joining: {gp.name} | id={gp.deviceId} | " +
                          $"product={gp.description.product} | interface={gp.description.interfaceName} | btn={btn}");
            pim.JoinPlayer(pairWithDevices: new InputDevice[] { gp });
            _lastJoinTime = now;
            assignedIds.Add(gp.deviceId);
        }
    }

    // East can't be both "instant join" and "hold to back out" on the same press — the instant
    // join would consume the press before a hold duration could ever be measured. So East's
    // join is deferred to release: release before HoldBackSeconds = tap = join (matches prior
    // behavior for raw-HID controllers, whose only join button is East); held past
    // HoldBackSeconds = back out to the carousel (Main scene) instead.
    private void UpdateHoldBackToCarousel(PlayerInputManager pim, HashSet<int> assignedIds)
    {
        if (assignedIds.Count > 0)
        {
            CancelHoldBack();
            return;
        }

        if (_holdBackDeviceId >= 0)
        {
            var held = InputSystem.devices.OfType<Gamepad>()
                .FirstOrDefault(g => g.deviceId == _holdBackDeviceId);

            if (held == null || _excludedIds.Contains(held.deviceId) || !held.buttonEast.isPressed)
            {
                // Released before the hold threshold (or device vanished) — treat as a tap: join.
                bool releasedNotDisconnected = held != null && !held.buttonEast.isPressed;
                CancelHoldBack();

                if (releasedNotDisconnected && Time.unscaledTime - _lastJoinTime >= GraceSeconds)
                {
                    if (GameFlowManager.VerboseLogging)
                        Debug.Log($"[JoinController] East tap-join: {held.name} | id={held.deviceId}");
                    pim.JoinPlayer(pairWithDevices: new InputDevice[] { held });
                    _lastJoinTime = Time.unscaledTime;
                }
                return;
            }

            float elapsed = Time.unscaledTime - _holdBackStartTime;
            ControlTutorialDirector.Instance?.UpdateAbortHoldUI(elapsed / HoldBackSeconds);

            if (elapsed >= HoldBackSeconds)
            {
                if (GameFlowManager.VerboseLogging)
                    Debug.Log($"[JoinController] East hold-back: returning to carousel (Main).");
                ControlTutorialDirector.Instance?.EndAbortHoldUI();
                _holdBackDeviceId = -1;
                SceneManager.LoadScene("Main");
            }
            return;
        }

        var starter = InputSystem.devices.OfType<Gamepad>()
            .FirstOrDefault(gp => !_excludedIds.Contains(gp.deviceId) && gp.buttonEast.wasPressedThisFrame);
        if (starter == null) return;

        _holdBackDeviceId = starter.deviceId;
        _holdBackStartTime = Time.unscaledTime;
        ControlTutorialDirector.Instance?.BeginAbortHoldUI("Back to Motif Selection", HoldBackSeconds);
    }

    private void CancelHoldBack()
    {
        if (_holdBackDeviceId < 0) return;
        _holdBackDeviceId = -1;
        ControlTutorialDirector.Instance?.CancelAbortHoldUI();
    }
}
