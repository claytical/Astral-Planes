using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>"Quit the game?" confirmation, shown through the generic <see cref="ModalMenu"/>.
/// Opened by a keyboard Escape tap (GlobalEscapeQuitListener) or the pause menu's Quit option.</summary>
public static class QuitPrompt
{
    static ModalMenuSpec _spec;

    public static bool IsShowing => _spec != null && ModalMenu.Current == _spec;

    /// <param name="pad">Gamepad allowed to answer; null = any gamepad.</param>
    public static void Show(Gamepad pad = null)
    {
        _spec = new ModalMenuSpec
        {
            title = "Quit?",
            pad = pad,
            defaultIndex = 1, // destructive choice isn't pre-highlighted
            options = new List<ModalMenuOption>
            {
                new ModalMenuOption("Yes", QuitNow),
                new ModalMenuOption("No"),
            },
        };
        ModalMenu.Show(_spec);
    }

    /// <summary>In-run quit (pause menu): confirm, then the same full teardown back to Main as the
    /// hold-East "Giving Up" abort (GameFlowManager.QuitToSelection).</summary>
    public static void ShowQuitToMainMenu(Gamepad pad = null)
    {
        _spec = new ModalMenuSpec
        {
            title = "Quit to Main Menu?",
            pad = pad,
            defaultIndex = 1,
            options = new List<ModalMenuOption>
            {
                new ModalMenuOption("Yes", () => GameFlowManager.Instance?.QuitToSelection()),
                new ModalMenuOption("No"),
            },
            keepOpen = () => GameFlowManager.Instance != null &&
                             GameFlowManager.Instance.CurrentState == GameState.Playing,
        };
        ModalMenu.Show(_spec);
    }

    /// <summary>Single real app-exit call site. Application.Quit() is ignored in the Editor, so
    /// there it exits Play mode instead.</summary>
    public static void QuitNow()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
