using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Replaces PlayerInputManager's automatic join detection with explicit per-frame scanning.
/// Prevents Steam Input from spawning duplicate players when a single physical gamepad
/// appears as both a raw HID device and a Steam virtual Xbox controller.
///
/// Requires PlayerInputManager.joinBehavior = JoinPlayersManually in the TrackSelection scene.
/// Self-installs via RuntimeInitializeOnLoadMethod — no scene setup needed.
/// </summary>
public class TrackSelectionJoinController : MonoBehaviour
{
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

    private void Update()
    {
        var pim = PlayerInputManager.instance;
        if (pim == null || !pim.joiningEnabled) return;

        if (GameFlowManager.Instance?.CurrentState != GameState.Selection) return;

        // Collect deviceIds already paired to a player.
        var assignedIds = new HashSet<int>();
        foreach (var lp in FindObjectsOfType<LocalPlayer>())
        {
            var pi = lp.GetComponent<PlayerInput>();
            if (pi == null) continue;
            foreach (var dev in pi.devices)
                assignedIds.Add(dev.deviceId);
        }

        // Find unassigned gamepads that pressed a join button this frame.
        var candidates = new List<Gamepad>();
        foreach (var device in InputSystem.devices)
        {
            if (assignedIds.Contains(device.deviceId)) continue;
            if (device is Gamepad gp &&
                (gp.buttonSouth.wasPressedThisFrame ||
                 gp.buttonEast.wasPressedThisFrame  ||
                 gp.startButton.wasPressedThisFrame))
            {
                candidates.Add(gp);
            }
        }

        if (candidates.Count == 0) return;

        // Remove candidates whose active button is also active on an already-assigned gamepad.
        // This blocks the delayed-frame case: physical fires frame N (joins), virtual fires frame
        // N+1 — the physical device is now assigned and still isPressed, so virtual is rejected.
        var filtered = candidates
            .Where(c => !MirrorsAssignedGamepad(c, assignedIds))
            .OrderBy(g => g.deviceId) // lower deviceId = physical HID (prefer it)
            .ToList();

        // Within a single frame, if multiple unassigned devices press the same button type
        // simultaneously, only the first (lowest deviceId) may join for that button.
        // This catches the same-frame case: physical + virtual both fire wasPressedThisFrame.
        var usedButtons = new HashSet<string>();
        foreach (var gp in filtered)
        {
            string btn = null;
            if      (gp.buttonSouth.wasPressedThisFrame && usedButtons.Add("south")) btn = "south";
            else if (gp.buttonEast.wasPressedThisFrame  && usedButtons.Add("east"))  btn = "east";
            else if (gp.startButton.wasPressedThisFrame && usedButtons.Add("start")) btn = "start";

            if (btn == null) continue; // duplicate button this frame — skip

            Debug.Log($"[JoinController] Joining device: {gp.name} | id={gp.deviceId} | btn={btn}");
            pim.JoinPlayer(pairWithDevices: new InputDevice[] { gp });

            // Mark this device as assigned so subsequent candidates in this loop see it.
            assignedIds.Add(gp.deviceId);
        }
    }

    // Returns true if any join-relevant button currently pressed on `candidate`
    // is also currently pressed on any already-assigned gamepad.
    private static bool MirrorsAssignedGamepad(Gamepad candidate, HashSet<int> assignedIds)
    {
        foreach (var lp in FindObjectsOfType<LocalPlayer>())
        {
            var pi = lp.GetComponent<PlayerInput>();
            if (pi == null) continue;
            foreach (var dev in pi.devices)
            {
                if (!assignedIds.Contains(dev.deviceId)) continue;
                if (dev is not Gamepad assignedGp) continue;
                if ((candidate.buttonSouth.isPressed && assignedGp.buttonSouth.isPressed) ||
                    (candidate.buttonEast.isPressed  && assignedGp.buttonEast.isPressed)  ||
                    (candidate.startButton.isPressed && assignedGp.startButton.isPressed))
                    return true;
            }
        }
        return false;
    }
}
