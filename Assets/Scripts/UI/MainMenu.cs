using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// The title menu in Main.unity (OPTIONS_MENU_DESIGN.md §11). It replaces "Press a button to
/// start" with:
///  • START: before the tutorial is done. Solo or co-op, then join screen, control sandbox and
///    the tutorial motif.
///  • CONTINUE: once it is done. Opens the motif carousel; the pick goes to the join screen.
///    Solo goes straight to the motif; multiplayer runs the sandbox first.
///  • VERSUS: only while 2+ input devices are connected. Survival or Score, then the carousel (or
///    the first motif after the tutorial if it isn't done), join screen (2+ players), sandbox,
///    then the motif.
///  • OPTIONS: the two-pane OptionsScreen, with DATA.
///
/// Created automatically whenever Main loads, so the scene needs no new wiring. Rows reuse
/// ModalMenu's row template and fonts (MenuKit). Input is raw-polled from any pad and the keyboard,
/// like ModalMenu. An Escape tap still opens the Quit prompt (GlobalEscapeQuitListener).
/// </summary>
public sealed class MainMenu : MonoBehaviour
{
    public const string SceneName = "Main";
    const string TrackSelectionScene = "TrackSelection";
    const float RowWidth = 620f;
    const float RowHeight = 96f;

    static MainMenu _instance;
    /// <summary>The title menu is on screen (not the carousel or Options).</summary>
    public static bool IsShowing => _instance != null && _instance._showing;

    enum Item { StartOrContinue, Versus, Options }

    readonly MenuNav _nav = new MenuNav { spaceConfirms = true };
    readonly List<Item> _items = new List<Item> { Item.StartOrContinue, Item.Versus, Item.Options };
    readonly List<ModalMenuButton> _rows = new List<ModalMenuButton>();
    readonly List<CanvasGroup> _rowGroups = new List<CanvasGroup>();
    CanvasGroup _group;
    PhaseLibraryCarousel _carousel;
    int _selected;
    bool _showing;
    bool _launching;
    int _shownFrame = -1;
    bool _tutorialComplete;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Hook()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != SceneName || _instance != null) return;
        var canvas = MenuKit.CreateCanvas("MainMenu", 400, out var group);
        SceneManager.MoveGameObjectToScene(canvas.gameObject, scene); // dies with Main
        _instance = canvas.gameObject.AddComponent<MainMenu>();
        _instance._group = group;
    }

    void Start()
    {
        // Back on the title screen: whatever was chosen last session no longer applies.
        StartIntent.Clear();
        PhaseLibraryStartConfig.Consume();

        _carousel = FindAnyObjectByType<PhaseLibraryCarousel>();
        // Read from disk, not the carousel: its Start may not have run yet this frame.
        _tutorialComplete = StartIntent.TutorialComplete;
        HideLegacyTitlePrompt();
        Build();
        Show(true);
    }

    void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // ---------------------------------------------------------------- build

    void Build()
    {
        if (ModalMenu.Prefab == null) { Debug.LogWarning("[MainMenu] Resources/UI/ModalMenu prefab missing; no title menu."); return; }
        var column = MenuKit.Column(transform, "Rows", new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), RowWidth, 20f);
        foreach (var item in _items)
        {
            var row = MenuKit.Row(column, item.ToString(), new Vector2(RowWidth, RowHeight), TextAlignmentOptions.Center);
            if (row == null) continue;
            row.SetLabel(Label(item));
            _rows.Add(row);
            _rowGroups.Add(row.gameObject.AddComponent<CanvasGroup>());
        }
    }

    string Label(Item item) => item switch
    {
        Item.StartOrContinue => _tutorialComplete ? "Continue" : "Start",
        Item.Versus => "Versus",
        _ => "Options",
    };

    // The old "Press a button to start" text, and its handler on the EventSystem, are retired.
    static void HideLegacyTitlePrompt()
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t && t.gameObject.scene.name == SceneName && t.name == "Start Text")
                t.gameObject.SetActive(false);
    }

    void Show(bool show)
    {
        _showing = show;
        if (_group != null) _group.alpha = show ? 1f : 0f;
        if (show)
        {
            _shownFrame = Time.frameCount;
            _nav.RequireRelease();
            if (!Enabled(_items[_selected])) _selected = 0;
            Refresh();
        }
        IntroTitleFader.SetVisible(show);
    }

    // ---------------------------------------------------------------- state

    bool Enabled(Item item) => item != Item.Versus || VersusAvailable;

    /// <summary>2+ devices that could each join: gamepads (raw HID duplicates of a Steam virtual pad
    /// don't count, same rule as the join screen) plus the keyboard.</summary>
    static bool VersusAvailable
    {
        get
        {
            var pads = Gamepad.all;
            bool hasVirtual = pads.Any(g => g.description.interfaceName != "HID");
            int count = pads.Count(g => !(hasVirtual && g.description.interfaceName == "HID"));
            if (Keyboard.current != null) count++;
            return count >= StartIntent.VersusMinPlayers;
        }
    }

    void Refresh()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            bool enabled = Enabled(_items[i]);
            _rows[i].SetSelected(i == _selected && enabled);
            _rowGroups[i].alpha = enabled ? 1f : 0.3f;
        }
    }

    // ---------------------------------------------------------------- input

    void Update()
    {
        if (!_showing || _launching || _rows.Count == 0) return;

        // Devices come and go: VERSUS may light up or go dark while the menu is open.
        if (!Enabled(_items[_selected])) Step(+1);
        Refresh();

        if (ModalMenu.ShouldSuppressInput || LivingScorePanel.ShouldSuppressConfirm) { _nav.RequireRelease(); return; }
        if (Time.frameCount == _shownFrame) return;

        var f = _nav.Poll();
        if (f.vertical != 0) { Step(f.vertical); Refresh(); return; }
        if (f.confirm) Choose(_items[_selected]);
    }

    void Step(int dir)
    {
        for (int n = 0; n < _items.Count; n++)
        {
            _selected = (_selected + dir + _items.Count) % _items.Count;
            if (Enabled(_items[_selected])) return;
        }
    }

    void Choose(Item item)
    {
        switch (item)
        {
            case Item.StartOrContinue:
                if (_tutorialComplete) { StartIntent.Set(StartKind.Continue); OpenCarousel(); }
                else { StartIntent.Set(StartKind.Start); GoToJoinScreen(); }
                break;
            case Item.Versus:
                ShowVersusChoice();
                break;
            case Item.Options:
                Show(false);
                OptionsMenu.OpenFromMainMenu(() => Show(true));
                if (!OptionsScreen.IsOpen) Show(true); // couldn't open (prefab missing)
                break;
        }
    }

    void ShowVersusChoice()
    {
        ModalMenu.Show(new ModalMenuSpec
        {
            title = "Versus",
            description = "Survival: the last ship standing wins.\nScore: the highest score wins when the motif completes.",
            options = new List<ModalMenuOption>
            {
                new ModalMenuOption("Survival", () => StartVersus(GameMode.CompetitiveElimination)),
                new ModalMenuOption("Score", () => StartVersus(GameMode.CompetitiveMotif)),
            },
            keepOpen = () => VersusAvailable,
        });
    }

    void StartVersus(GameMode mode)
    {
        StartIntent.Set(StartKind.Versus, mode);
        if (_tutorialComplete) { OpenCarousel(); return; }

        // Tutorial not done: the carousel only offers the tutorial, which isn't a versus arena.
        // Start on the first motif after it.
        if (!RequestFirstMotifAfterTutorial())
            Debug.LogWarning("[MainMenu] No phase 1 motif in the PhaseLibrary; VERSUS falls back to the default start.");
        GoToJoinScreen();
    }

    bool RequestFirstMotifAfterTutorial()
    {
        var lib = _carousel != null ? _carousel.Library : null;
        var phases = lib != null ? lib.phases : null;
        if (phases == null || phases.Count < 2) return false;
        var phase = phases[1];
        if (phase?.motifs == null || phase.motifs.Count == 0 || phase.motifs[0] == null) return false;
        PhaseLibraryStartConfig.RequestStart(phase.phaseId, 1, 0, phase.motifs[0].motifId);
        return true;
    }

    void OpenCarousel()
    {
        if (_carousel == null || !_carousel.Engage(() => { StartIntent.Clear(); Show(true); }))
        {
            // Nothing to pick (shouldn't happen once the tutorial is done): go on with the default start.
            GoToJoinScreen();
            return;
        }
        Show(false);
    }

    void GoToJoinScreen()
    {
        _launching = true;
        ControlTutorialDirector.Instance?.HidePrimary();
        GameFlowManager.Instance?.StartShipSelectionPhase();
        SceneManager.LoadScene(TrackSelectionScene);
    }
}
