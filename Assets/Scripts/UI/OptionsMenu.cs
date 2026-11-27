using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Options and the in-run Paused menu (OPTIONS_MENU_DESIGN.md §11).
///
/// Options is the two-pane <see cref="OptionsScreen"/>: AUDIO, CONTROLS, DISPLAY, and DATA
/// (DATA only in Main, outside a run, because Reset reloads the scene and Main is the only scene
/// where that's a clean restart). It opens from:
///  • Global/OpenSettings (gamepad Select, keyboard F1 / O) outside a run, and the main menu's
///    OPTIONS row (<see cref="OpenFromMainMenu"/>). The subscription is installed once by the
///    SettingsPanelController bootstrap in Main.unity and outlives scene loads.
///  • The Paused menu's Options row during a run.
///
/// During a run, Select / P (the player's own Play/Pause, or Global for a keyboard player) opens
/// a ModalMenu titled "Paused": Resume, Options, Quit to Main Menu. It is a soft pause: time and
/// music keep going, and only gameplay input is locked. A keyboard Escape tap opens Paused with
/// Quit highlighted in a run, and the Quit prompt anywhere else.
/// </summary>
public static class OptionsMenu
{
    const string AudioLabel = "Audio";
    const string ControlsLabel = "Controls";
    const string DisplayLabel = "Display";
    const string DataLabel = "Data";
    const string OptionsLabel = "Options";
    const float VolumeStep = 0.1f;
    const float WithdrawTimeoutSeconds = 20f;

    static InputAction _openAction;
    static ModalMenuSpec _current;   // last Paused spec shown; Select closes it
    static int _openedFrame = -1;
    static LocalPlayer _opener;
    static bool _hadOpener;          // Unity's == null can't tell "never set" from "destroyed"
    static Gamepad _pad;
    static bool _inRun;
    static bool? _windowedRequested; // Screen.fullScreenMode updates a frame late
    static RefDevice _refDevice;
    static System.Action _screenBack; // where Back on the category column goes (Paused, main menu)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        if (_openAction != null) _openAction.started -= OnOpenPressed;
        _openAction = null;
        _current = null;
        _openedFrame = -1;
        _opener = null;
        _pad = null;
        _windowedRequested = null;
        _screenBack = null;
    }

    /// <summary>Subscribes to Global/OpenSettings once per play session.</summary>
    public static void Install(InputActionAsset actions)
    {
        if (_openAction != null || actions == null) return;
        var action = actions.FindActionMap("Global")?.FindAction("OpenSettings");
        if (action == null)
        {
            Debug.LogWarning("[OptionsMenu] Global/OpenSettings action not found.");
            return;
        }
        _openAction = action;
        _openAction.started += OnOpenPressed;
        _openAction.Enable();
    }

    static void OnOpenPressed(InputAction.CallbackContext ctx)
    {
        if (IsRunActive())
        {
            // Gamepad Select in a run belongs to that player's own Play/Pause binding.
            if (!(ctx.control?.device is Keyboard)) return;
            Toggle(FindKeyboardPlayer());
            return;
        }
        Toggle(null);
    }

    /// <summary>Keyboard Escape tap: Paused with Quit highlighted in a run; the Quit prompt elsewhere.</summary>
    public static void OpenFromEscape()
    {
        if (ModalMenu.IsOpen) return;
        if (IsRunActive()) Toggle(FindKeyboardPlayer(), focusQuit: true);
        else if (!LivingScorePanel.ShouldSuppressConfirm) QuitPrompt.Show();
    }

    /// <summary>Main menu OPTIONS row. Back on the category column runs <paramref name="onBack"/>.</summary>
    public static void OpenFromMainMenu(System.Action onBack)
    {
        if (ModalMenu.IsOpen || IsRunActive()) return;
        SetContext(null);
        _screenBack = onBack;
        if (OpenScreen()) _openedFrame = Time.frameCount;
    }

    /// <summary>In a run: opens Paused for <paramref name="opener"/>. Outside a run: opens the Options
    /// screen. Closes either one if it is showing.</summary>
    public static void Toggle(LocalPlayer opener, bool focusQuit = false)
    {
        if (OptionsScreen.IsOpen)
        {
            if (Time.frameCount == _openedFrame) return; // same press seen twice (Steam Deck dual device)
            OptionsScreen.Close();
            return;
        }
        if (ModalMenu.IsOpen)
        {
            if (Time.frameCount == _openedFrame) return;
            if (_current != null && ModalMenu.Current == _current) ModalMenu.Close();
            return; // some other modal owns the screen
        }
        if (ModalMenu.ShouldSuppressInput) return; // a modal closed on this same press
        if (LivingScorePanel.ShouldSuppressConfirm) return;
        // Match results: MatchResultsPanel treats any button (Select included) as "back to Main".
        if (GameFlowManager.Instance != null && GameFlowManager.Instance.MatchEnded) return;

        SetContext(opener);
        _screenBack = null;
        bool shown = _inRun ? ShowPaused(focusQuit ? QuitLabel : null) : OpenScreen();
        if (!shown) return;
        _openedFrame = Time.frameCount;

        if (_inRun)
        {
            // Stop any boost already in progress; input is locked until the menu closes.
            var vehicles = GameFlowManager.Instance.GetVehicles();
            if (vehicles != null)
                for (int i = 0; i < vehicles.Count; i++)
                    if (vehicles[i] != null) vehicles[i].TurnOffBoost();
        }
    }

    static void SetContext(LocalPlayer opener)
    {
        _inRun = IsRunActive();
        if (_inRun && opener == null) opener = FindKeyboardPlayer();
        _opener = _inRun ? opener : null;
        _hadOpener = _opener != null;
        _pad = _opener != null ? _opener.PrimaryGamepad() : null;
    }

    // ---------------------------------------------------------------- context

    static bool IsRunActive()
    {
        var gfm = GameFlowManager.Instance;
        return gfm != null && gfm.CurrentState == GameState.Playing && !gfm.MatchEnded;
    }

    static bool DataAvailable => !_inRun && SceneManager.GetActiveScene().name == MainMenu.SceneName;

    const string QuitLabel = "Quit to Main Menu";

    static LocalPlayer FindKeyboardPlayer()
    {
        foreach (var lp in Object.FindObjectsByType<LocalPlayer>())
            if (lp != null && lp.IsKbmPlayer) return lp;
        return null;
    }

    static bool StillValid()
    {
        if (!_inRun) return !IsRunActive(); // a run started underneath: get out of the way
        return IsRunActive() && (!_hadOpener || _opener != null); // opener left (destroyed): close
    }

    /// <summary>Common ModalMenu spec setup: owner pad, context guard, and a settings flush on close.</summary>
    static bool Show(ModalMenuSpec spec)
    {
        spec.pad = _pad;
        spec.keepOpen = StillValid;
        spec.onClosed = GameSettings.Flush;
        _current = spec;
        return ModalMenu.Show(spec);
    }

    static int IndexOf(List<ModalMenuOption> options, string label)
    {
        if (label == null) return 0;
        int i = options.FindIndex(o => o.label == label);
        return i < 0 ? 0 : i;
    }

    // ---------------------------------------------------------------- paused (in a run)

    static bool ShowPaused(string focus = null)
    {
        var options = new List<ModalMenuOption>
        {
            new ModalMenuOption("Resume"),
            new ModalMenuOption(OptionsLabel, () => { _screenBack = () => ShowPaused(OptionsLabel); OpenScreen(); }),
            new ModalMenuOption(QuitLabel, () => QuitPrompt.ShowQuitToMainMenu(_pad)),
        };
        return Show(new ModalMenuSpec
        {
            title = "Paused",
            options = options,
            defaultIndex = IndexOf(options, focus),
        });
    }

    // ---------------------------------------------------------------- options screen

    static bool OpenScreen(string category = null, string rowFocus = null)
    {
        var categories = new List<OptionsCategory>
        {
            new OptionsCategory { name = AudioLabel, rows = AudioRows,
                footer = () => "Preview Beat adds a drum loop to ring previews on the motif carousel." },
            new OptionsCategory { name = ControlsLabel, rows = ControlRows,
                footer = () => ReferenceText(_refDevice) + "\n\nGroove Style is where ship select starts; each player can still switch it there." },
        };
        if (!Application.isMobilePlatform)
            categories.Add(new OptionsCategory { name = DisplayLabel, rows = DisplayRows });
        if (DataAvailable)
            categories.Add(new OptionsCategory { name = DataLabel, rows = DataRows,
                footer = () => "Publishing shares your flight records with the Living Score.\nResetting progress erases every ring; your settings are kept." });

        _refDevice = DefaultReferenceDevice();

        int catIndex = Mathf.Max(0, categories.FindIndex(c => c.name == category));
        int rowIndex = -1;
        if (rowFocus != null)
            rowIndex = categories[catIndex].rows().FindIndex(r => r.title == rowFocus);

        return OptionsScreen.Open(new OptionsScreenSpec
        {
            title = OptionsLabel,
            categories = categories,
            categoryIndex = catIndex,
            rowIndex = rowIndex,
            pad = _pad,
            keepOpen = StillValid,
            onBack = _screenBack,
            onClosed = GameSettings.Flush,
        });
    }

    static string Percent(float v) => Mathf.RoundToInt(v * 100f) + "%";
    static string OnOff(bool on) => on ? "On" : "Off";

    static List<OptionsRow> AudioRows() => new List<OptionsRow>
    {
        OptionsRow.Value("Master", () => Percent(GameSettings.MasterVolume),
            d => GameSettings.MasterVolume += d * VolumeStep, fill: () => GameSettings.MasterVolume),
        OptionsRow.Value("Music", () => Percent(GameSettings.MusicVolume),
            d => GameSettings.MusicVolume += d * VolumeStep, fill: () => GameSettings.MusicVolume),
        OptionsRow.Value("Effects", () => Percent(GameSettings.EffectsVolume),
            d => GameSettings.EffectsVolume += d * VolumeStep, fill: () => GameSettings.EffectsVolume),
        OptionsRow.Value("Preview Beat", () => OnOff(GameSettings.PreviewBeat),
            d => GameSettings.PreviewBeat = !GameSettings.PreviewBeat, repeats: false),
    };

    static List<OptionsRow> DisplayRows() => new List<OptionsRow>
    {
        OptionsRow.Value("Window Mode", () => IsWindowed() ? "Windowed" : "Fullscreen",
            d => SetWindowed(!IsWindowed()), repeats: false),
    };

    static bool IsWindowed() => _windowedRequested ?? Screen.fullScreenMode == FullScreenMode.Windowed;

    // Unity persists the window mode itself between launches, so this isn't in settings.json.
    static void SetWindowed(bool windowed)
    {
        _windowedRequested = windowed;
        int sw = Display.main.systemWidth, sh = Display.main.systemHeight;
        if (windowed)
        {
            int w = Mathf.RoundToInt(sw * 0.75f);
            int h = Mathf.Min(Mathf.RoundToInt(w * 9f / 16f), Mathf.RoundToInt(sh * 0.75f));
            Screen.SetResolution(w, h, FullScreenMode.Windowed);
        }
        else
        {
            Screen.SetResolution(sw, sh, FullScreenMode.FullScreenWindow);
        }
    }

    // ---------------------------------------------------------------- controls

    static string GrooveName(NotePlacementMethod m) =>
        m == NotePlacementMethod.Manual ? "Groove Commander" : "Cozy Co-Pilot";

    static List<OptionsRow> ControlRows()
    {
        var rows = new List<OptionsRow>
        {
            OptionsRow.Value("Groove Style", () => GrooveName(GameSettings.GrooveStyle),
                d => GameSettings.GrooveStyle = GameSettings.GrooveStyle == NotePlacementMethod.Manual
                    ? NotePlacementMethod.Auto : NotePlacementMethod.Manual, repeats: false),
        };

        // Only offer the keyboard scheme when a keyboard exists, and never let it pick Mouse
        // without a mouse (that choice used to save but silently never apply).
        if (Keyboard.current != null)
            rows.Add(OptionsRow.Value("Keyboard Scheme", () => ControlPrefs.Scheme == KbmScheme.Mouse ? "Mouse" : "Keyboard",
                d =>
                {
                    if (ControlPrefs.Scheme == KbmScheme.Mouse) ControlPrefs.Scheme = KbmScheme.Keyboard;
                    else if (Mouse.current != null) ControlPrefs.Scheme = KbmScheme.Mouse;
                }, repeats: false));

        // Cycles which device the reference text under the rows describes.
        rows.Add(OptionsRow.Value("Control Reference", () => _refDevice.ToString(),
            d => _refDevice = (RefDevice)(((int)_refDevice + d + 3) % 3), repeats: false));
        return rows;
    }

    enum RefDevice { Gamepad, Keyboard, Mouse }

    static RefDevice DefaultReferenceDevice()
    {
        if (_opener != null && _opener.IsKbmPlayer)
            return ControlPrefs.Scheme == KbmScheme.Mouse ? RefDevice.Mouse : RefDevice.Keyboard;
        if (Gamepad.all.Count > 0) return RefDevice.Gamepad;
        return ControlPrefs.Scheme == KbmScheme.Mouse ? RefDevice.Mouse : RefDevice.Keyboard;
    }

    static string ReferenceText(RefDevice d)
    {
        switch (d)
        {
            case RefDevice.Keyboard:
                return "A / D  Turn     W / S  Drift / turn around\nShift  Boost     Space  Place note\nP, O, F1 or Esc  Pause / Options";
            case RefDevice.Mouse:
                return "Ship flies toward the cursor\nLeft click  Boost     Right click  Place note\nP, O, F1 or Esc  Pause / Options";
            default:
                return "Left stick  Steer     Right trigger  Boost\nConfirm  Place note     Back  Undo (hold to leave)\nSelect  Pause / Options";
        }
    }

    // ---------------------------------------------------------------- data (publishing + reset)

    static bool PublishOn => TelemetryConsent.HasDecided && TelemetryConsent.PublishFlightPlansEnabled;

    const string PublishLabel = "Publish Flight Records";
    const string RestoreLabel = "Restore Default Settings";
    const string ResetLabel = "Reset All Progress";

    static List<OptionsRow> DataRows() => new List<OptionsRow>
    {
        OptionsRow.Value(PublishLabel, () => OnOff(PublishOn), d => SetPublish(!PublishOn), repeats: false),
        OptionsRow.Action(RestoreLabel, ShowRestoreConfirm),
        OptionsRow.Action(ResetLabel, ShowResetConfirm),
    };

    /// <summary>Back to the DATA panel after a confirm prompt, with <paramref name="row"/> focused.</summary>
    static void ShowData(string row) => OpenScreen(DataLabel, row);

    static void SetPublish(bool publish)
    {
        bool wasOn = TelemetryConsent.PublishFlightPlansEnabled;
        TelemetryConsent.Decide(publish);
        var uploader = Object.FindAnyObjectByType<MazeTelemetryUploader>();
        if (publish) uploader?.FlushPendingNow();
        else if (wasOn) uploader?.RemovePublishedFlightPlans();
    }

    static void ShowRestoreConfirm()
    {
        Show(new ModalMenuSpec
        {
            title = RestoreLabel,
            description = "Reset volume, controls and Groove Style to their defaults? Your rings and your publish choice are kept.",
            options = new List<ModalMenuOption>
            {
                new ModalMenuOption("Cancel", () => ShowData(RestoreLabel)),
                new ModalMenuOption("Restore Defaults", () =>
                {
                    GameSettings.RestoreDefaults();
                    ShowData(RestoreLabel);
                }),
            },
            onCancel = () => ShowData(RestoreLabel),
        });
    }

    static void ShowResetConfirm()
    {
        var options = new List<ModalMenuOption>
        {
            new ModalMenuOption("Cancel", () => ShowData(ResetLabel)),
            new ModalMenuOption("Erase Progress", ResetAndReload),
        };
        // Q1: forgetting the anonymous publisher identity is only offered while publishing, and
        // only after the server confirms the withdrawal (otherwise those records are orphaned).
        if (TelemetryConsent.PublishFlightPlansEnabled)
            options.Add(new ModalMenuOption("Erase & Withdraw Published Records", EraseAndWithdraw));

        Show(new ModalMenuSpec
        {
            title = ResetLabel,
            description = "Erase every ring and start over? Your settings are kept. This can't be undone.",
            options = options,
            onCancel = () => ShowData(ResetLabel),
        });
    }

    static void ResetAndReload()
    {
        SaveDataReset.ResetProgress();
        // Carousel, title mode and tutorial gating all read the ring files on scene start.
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    static void EraseAndWithdraw()
    {
        var uploader = Object.FindAnyObjectByType<MazeTelemetryUploader>();
        var host = GameSettings.GameSettingsRuntime.Instance;
        if (uploader == null || host == null) { ShowWithdrawFailed(); return; }

        var waiting = new ModalMenuSpec
        {
            title = "Withdrawing Published Records",
            description = "Contacting the Living Score…",
            cancelable = false,
        };
        waiting.options.Add(new ModalMenuOption("Please Wait", () => Show(waiting)));
        Show(waiting);

        bool done = false;
        uploader.RemovePublishedFlightPlans(ok =>
        {
            if (done) return;
            done = true;
            if (!ok) { ShowWithdrawFailed(); return; }
            PublisherIdentity.Forget();
            TelemetryConsent.Decide(false);
            ResetAndReload();
        });
        host.StartCoroutine(WithdrawTimeout(() =>
        {
            if (done) return;
            done = true;
            ShowWithdrawFailed();
        }));
    }

    static IEnumerator WithdrawTimeout(System.Action onTimeout)
    {
        yield return new WaitForSecondsRealtime(WithdrawTimeoutSeconds);
        onTimeout();
    }

    static void ShowWithdrawFailed()
    {
        Show(new ModalMenuSpec
        {
            title = "Couldn't Reach the Living Score",
            description = "Your published records weren't withdrawn, so your publisher identity was kept. Nothing has been erased yet.",
            options = new List<ModalMenuOption>
            {
                new ModalMenuOption("Try Again", EraseAndWithdraw),
                new ModalMenuOption("Erase Progress Only", ResetAndReload),
                new ModalMenuOption("Cancel", () => ShowData(ResetLabel)),
            },
            defaultIndex = 2,
            onCancel = () => ShowData(ResetLabel),
        });
    }
}
