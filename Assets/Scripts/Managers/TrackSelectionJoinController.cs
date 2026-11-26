using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Replaces PlayerInputManager's automatic join detection with explicit per-frame scanning.
///
/// Native Steam Input path (SteamInputManager.DrivesGameplay): every controller is exactly one
/// SteamInputGamepad and all native gamepads are disabled, so the duplicate handling below is
/// inert — disabled devices never report presses. Buttons are read through PadSemantics, so
/// "confirm" is Choose (Switch A under Steam's Switch config) and "back" is Back.
///
/// Fallback path (no Steam Input) — the rest of this note applies:
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
///   5. A gamepad whose player backed out while others stayed in must HOLD confirm
///      (RejoinHoldSeconds) to rejoin; see UpdateRejoinHold(). A tap used to rejoin it
///      instantly, which on Switch (A = confirm = join) brought back a player who had just left.
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

    // Gamepads whose player backed out while someone else stayed in. A tap of confirm (A on
    // Switch) would instantly rejoin them, which is exactly what a player who just left is
    // likely to mash next, so they must hold confirm for RejoinHoldSeconds instead. Fresh
    // gamepads still join on a tap. Cleared when nobody is left (nothing to protect).
    private readonly HashSet<int> _lastAssignedIds = new();
    private readonly HashSet<int> _leftDeviceIds = new();
    private int _rejoinDeviceId = -1;
    private float _rejoinStartTime;
    private const float RejoinHoldSeconds = 1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RegisterHook()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded; // idempotent if domain reload is disabled
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

        // The tutorial roster is fixed when it begins; a mid-tutorial joiner would have no ship
        // and no tutorial participation.
        if (GameFlowManager.Instance?.CurrentState != GameState.Selection
            || (ControlTutorialDirector.Instance != null && ControlTutorialDirector.Instance.IsPrimaryTutorialRunning))
        {
            CancelRejoinHold(); // e.g. the remaining player readied up and the tutorial began
            return;
        }

        var assignedIds = new HashSet<int>();
        foreach (var lp in FindObjectsByType<LocalPlayer>())
        {
            var pi = lp.GetComponent<PlayerInput>();
            if (pi == null) continue;
            foreach (var dev in pi.devices)
                assignedIds.Add(dev.deviceId);
        }

        TrackLeftDevices(assignedIds);

        UpdateHoldBackToCarousel(pim, assignedIds);
        UpdateRejoinHold(pim, assignedIds);
        TryJoinKeyboardMouse(pim, assignedIds);

        // Confirm and start are instant join triggers. Back is handled by
        // UpdateHoldBackToCarousel above: a quick tap joins (same as before), a sustained hold
        // backs out to the carousel instead — see that method for why Back can't also be
        // instant here. Gamepads that just backed out are handled by UpdateRejoinHold.
        var candidates = InputSystem.devices
            .OfType<Gamepad>()
            .Where(gp => !assignedIds.Contains(gp.deviceId) &&
                         !_excludedIds.Contains(gp.deviceId) &&
                         !_leftDeviceIds.Contains(gp.deviceId) &&
                         (PadSemantics.ConfirmButton(gp).wasPressedThisFrame ||
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
            if      (PadSemantics.ConfirmButton(gp).wasPressedThisFrame && usedButtons.Add("south")) btn = "south";
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

            if (held == null || _excludedIds.Contains(held.deviceId) || !PadSemantics.BackButton(held).isPressed)
            {
                // Released before the hold threshold (or device vanished) — treat as a tap: join.
                bool releasedNotDisconnected = held != null && !PadSemantics.BackButton(held).isPressed;
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
            .FirstOrDefault(gp => !_excludedIds.Contains(gp.deviceId) && PadSemantics.BackButton(gp).wasPressedThisFrame);
        if (starter == null) return;

        _holdBackDeviceId = starter.deviceId;
        _holdBackStartTime = Time.unscaledTime;
        ControlTutorialDirector.Instance?.BeginAbortHoldUI("Back to Motif Selection", HoldBackSeconds);
    }

    // A gamepad that was paired to a player last frame and isn't now (its player backed out)
    // must hold confirm to rejoin while anyone else is still in.
    private void TrackLeftDevices(HashSet<int> assignedIds)
    {
        if (assignedIds.Count == 0)
        {
            _leftDeviceIds.Clear();
        }
        else
        {
            foreach (int id in _lastAssignedIds)
            {
                if (assignedIds.Contains(id) || InputSystem.GetDeviceById(id) == null) continue;
                if (_leftDeviceIds.Add(id))
                    Debug.Log($"[JoinController] Device id={id} backed out; holding confirm is required to rejoin.");
            }
        }

        _lastAssignedIds.Clear();
        _lastAssignedIds.UnionWith(assignedIds);
    }

    private void UpdateRejoinHold(PlayerInputManager pim, HashSet<int> assignedIds)
    {
        if (_rejoinDeviceId >= 0)
        {
            var held = InputSystem.GetDeviceById(_rejoinDeviceId);
            if (held == null || assignedIds.Count == 0 || !_leftDeviceIds.Contains(held.deviceId)
                || !IsJoinConfirmHeld(held))
            {
                CancelRejoinHold();
                return;
            }

            float elapsed = Time.unscaledTime - _rejoinStartTime;
            ControlTutorialDirector.Instance?.UpdateRejoinHoldUI(elapsed / RejoinHoldSeconds);
            if (elapsed < RejoinHoldSeconds) return;

            _rejoinDeviceId = -1;
            ControlTutorialDirector.Instance?.EndRejoinHoldUI();
            _leftDeviceIds.Remove(held.deviceId);
            Debug.Log($"[JoinController] Rejoin (held): {held.name} | id={held.deviceId}");
            if (held is Keyboard) JoinKeyboardMouse(pim, assignedIds);
            else
            {
                pim.JoinPlayer(pairWithDevices: new InputDevice[] { held });
                _lastJoinTime = Time.unscaledTime;
                assignedIds.Add(held.deviceId);
            }
            return;
        }

        if (assignedIds.Count == 0) return;

        foreach (var gp in InputSystem.devices.OfType<Gamepad>())
        {
            if (!_leftDeviceIds.Contains(gp.deviceId) || assignedIds.Contains(gp.deviceId)) continue;
            if (!PadSemantics.ConfirmButton(gp).wasPressedThisFrame) continue;

            _rejoinDeviceId = gp.deviceId;
            _rejoinStartTime = Time.unscaledTime;
            ControlTutorialDirector.Instance?.BeginRejoinHoldUI(gp);
            return;
        }

        var kb = Keyboard.current;
        if (kb != null && _leftDeviceIds.Contains(kb.deviceId) && !assignedIds.Contains(kb.deviceId)
            && KeyboardMouseJoinPressedThisFrame())
        {
            _rejoinDeviceId = kb.deviceId;
            _rejoinStartTime = Time.unscaledTime;
            ControlTutorialDirector.Instance?.BeginRejoinHoldUI(null);
        }
    }

    // ---- Keyboard & mouse player ----
    // One player pairs BOTH Keyboard.current and Mouse.current; the control scheme ("Keyboard" or
    // "Mouse", Settings → Keyboard Controls) is only a binding mask. Joins on Enter / Space / left
    // click, under the same grace cooldown as gamepads. A keyboard player who backed out while
    // others stayed in must hold the join input to rejoin (UpdateRejoinHold), like a gamepad.
    private void TryJoinKeyboardMouse(PlayerInputManager pim, HashSet<int> assignedIds)
    {
        var kb = Keyboard.current;
        if (kb == null || assignedIds.Contains(kb.deviceId) || _leftDeviceIds.Contains(kb.deviceId)) return;
        if (Mouse.current != null && assignedIds.Contains(Mouse.current.deviceId)) return;
        if (ModalMenu.IsOpen || ModalMenu.ShouldSuppressInput) return;
        if (!KeyboardMouseJoinPressedThisFrame()) return;
        if (Time.unscaledTime - _lastJoinTime < GraceSeconds) return;

        JoinKeyboardMouse(pim, assignedIds);
    }

    private void JoinKeyboardMouse(PlayerInputManager pim, HashSet<int> assignedIds)
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        var devices = Mouse.current != null
            ? new InputDevice[] { kb, Mouse.current }
            : new InputDevice[] { kb };

        // The Mouse scheme requires a mouse; without one, fall back to the Keyboard scheme.
        string scheme = Mouse.current != null ? ControlPrefs.SchemeName : ControlPrefs.KeyboardSchemeName;
        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[JoinController] Joining keyboard/mouse player | scheme={scheme}");
        pim.JoinPlayer(controlScheme: scheme, pairWithDevices: devices);
        _lastJoinTime = Time.unscaledTime;
        foreach (var d in devices) assignedIds.Add(d.deviceId);
    }

    private static bool KeyboardMouseJoinPressedThisFrame()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        return (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame
                               || kb.spaceKey.wasPressedThisFrame))
            || (mouse != null && mouse.leftButton.wasPressedThisFrame);
    }

    private static bool IsJoinConfirmHeld(InputDevice device)
    {
        switch (device)
        {
            case Gamepad gp:
                return PadSemantics.ConfirmButton(gp).isPressed;
            case Keyboard kb:
                return kb.enterKey.isPressed || kb.numpadEnterKey.isPressed || kb.spaceKey.isPressed
                    || (Mouse.current != null && Mouse.current.leftButton.isPressed);
            default:
                return false;
        }
    }

    private void CancelRejoinHold()
    {
        if (_rejoinDeviceId < 0) return;
        _rejoinDeviceId = -1;
        ControlTutorialDirector.Instance?.EndRejoinHoldUI();
    }

    private void OnDisable() => CancelRejoinHold();

    private void CancelHoldBack()
    {
        if (_holdBackDeviceId < 0) return;
        _holdBackDeviceId = -1;
        ControlTutorialDirector.Instance?.CancelAbortHoldUI();
    }
}
