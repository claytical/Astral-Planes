using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Event-gated narrated tutorial for the Phase 0 motif in GeneratedTrack. Listen-only: it never
/// touches musical state. Gameplay signals arrive via <see cref="TutorialEvents"/> and are latched.
/// The active stage is always derived from the latches (highest completed gate + 1), so narration
/// can never lag behind the player: obsolete lines are dropped, not shown stale.
/// </summary>
public class NarratedTutorialController : MonoBehaviour
{
    const string GameplayScene = "GeneratedTrack";

    enum Interruption { None, Escaped, Discarded }

    NarratedTutorialScript _script;
    TutorialPromptView _view;
    readonly HashSet<TutorialGate> _latched = new HashSet<TutorialGate>();
    int _carvedCells;
    Interruption _pending = Interruption.None;
    bool _playingInterruption;
    int _stage;
    GameFlowManager _gfm;

    // Highlight targets remembered so a stage can re-pulse them when its prompt appears.
    Transform _armedStar, _firstNote, _placeholder;

    // The motif-start ring record: the first message waits until it has faded away.
    bool _introHidden;
    const float IntroHiddenTimeout = 30f; // safety: never let a missed signal block the tutorial forever

    // Delay-skip state (commit pressed with no carried note).
    bool _skipRequested;
    float _lineShownTime;

    // Placement-window cue state.
    int _placedSinceCue;
    int _cueCompletedStage = -1;
    Vehicle[] _vehicleCache = System.Array.Empty<Vehicle>();
    float _vehicleCacheTime = -10f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != GameplayScene) return;
        if (FindFirstObjectByType<NarratedTutorialController>() != null) return;
        new GameObject("NarratedTutorialController").AddComponent<NarratedTutorialController>();
    }

    void OnIntroRecordHidden() => _introHidden = true;

    IEnumerator Start()
    {
        // Subscribe immediately: the rings can finish fading while we are still loading below.
        TutorialEvents.IntroRecordHidden += OnIntroRecordHidden;

        // Only run on the Phase 0 tutorial motif. Wait for the phase manager to apply the motif.
        PhaseTransitionManager ptm = null;
        float waited = 0f;
        while (waited < 15f)
        {
            if (ptm == null) ptm = FindFirstObjectByType<PhaseTransitionManager>();
            if (ptm != null && ptm.CurrentPhaseIndex >= 0) break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        if (ptm == null || ptm.CurrentPhaseIndex != 0) { Destroy(gameObject); yield break; }

        _script = NarratedTutorialScript.Load();
        if (_script == null) { Destroy(gameObject); yield break; } // Load() already logged why

        _view = FindAuthoredView() ?? TutorialPromptView.Create(transform);
        Subscribe();
        _gfm = GameFlowManager.Instance;
        if (_gfm != null) _gfm.OnMotifCompleted += HandleRecordComplete;

        // Hold the first message until the intro rings have disappeared.
        float introWait = 0f;
        while (!_introHidden && introWait < IntroHiddenTimeout)
        {
            introWait += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!_introHidden)
            Debug.LogWarning("[NarratedTutorial] Intro rings never reported hidden; starting the tutorial anyway.");
        else if (_script.introSettleSeconds > 0f)
            yield return new WaitForSecondsRealtime(_script.introSettleSeconds);

        yield return Run();
    }

    // Binds to the authored panel (e.g. Canvas/UI/Tutorial + its TMP child), without editing the scene.
    TutorialPromptView FindAuthoredView()
    {
        string path = string.IsNullOrEmpty(_script.panelPath) ? "Canvas/UI/Tutorial" : _script.panelPath;
        int slash = path.IndexOf('/');
        string rootName = slash < 0 ? path : path.Substring(0, slash);
        string rest = slash < 0 ? null : path.Substring(slash + 1);

        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name != rootName) continue;
            // Transform.Find reaches inactive children, unlike GameObject.Find.
            var panel = rest == null ? root.transform : root.transform.Find(rest);
            if (panel == null) continue;
            var label = panel.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (label == null) continue;
            return TutorialPromptView.Bind(panel, label);
        }

        Debug.LogWarning($"[NarratedTutorial] Authored panel '{path}' (with a TMP_Text child) not found in '{SceneManager.GetActiveScene().name}'; using a generated overlay.");
        return null;
    }

    void OnDestroy()
    {
        if (_view != null) _view.Deactivate();
        TutorialEvents.IntroRecordHidden -= OnIntroRecordHidden;
        Unsubscribe();
        TutorialHighlightRing.CancelAll();
        if (_gfm != null) _gfm.OnMotifCompleted -= HandleRecordComplete;
    }

    void HandleRecordComplete()
    {
        // Record finished: the tutorial's job is done. Existing flow owns completion/saving.
        StopAllCoroutines();
        _view?.HideImmediate();
        Destroy(gameObject, 0.1f);
    }

    // ---- event wiring -------------------------------------------------------------------

    void Subscribe()
    {
        TutorialEvents.CellCarvedByVehicle += OnCarved;
        TutorialEvents.TentacleAppeared    += OnTentacle;
        TutorialEvents.StarArmed           += OnStarArmed;
        TutorialEvents.StarPoked           += OnStarPoked;
        TutorialEvents.DiscoveryBurst      += OnBurst;
        TutorialEvents.FirstNoteSpawned    += OnFirstNoteSpawned;
        TutorialEvents.NoteCaught          += OnCaught;
        TutorialEvents.NotePlaced          += OnPlaced;
        TutorialEvents.RingCompleted       += OnRing;
        TutorialEvents.DiscoveryEscaped    += OnEscaped;
        TutorialEvents.NoteDiscarded       += OnDiscarded;
        TutorialEvents.CommitPressedWithoutNote += OnCommitWithoutNote;
    }

    void Unsubscribe()
    {
        TutorialEvents.CellCarvedByVehicle -= OnCarved;
        TutorialEvents.TentacleAppeared    -= OnTentacle;
        TutorialEvents.StarArmed           -= OnStarArmed;
        TutorialEvents.StarPoked           -= OnStarPoked;
        TutorialEvents.DiscoveryBurst      -= OnBurst;
        TutorialEvents.FirstNoteSpawned    -= OnFirstNoteSpawned;
        TutorialEvents.NoteCaught          -= OnCaught;
        TutorialEvents.NotePlaced          -= OnPlaced;
        TutorialEvents.RingCompleted       -= OnRing;
        TutorialEvents.DiscoveryEscaped    -= OnEscaped;
        TutorialEvents.NoteDiscarded       -= OnDiscarded;
        TutorialEvents.CommitPressedWithoutNote -= OnCommitWithoutNote;
    }

    void OnCarved()
    {
        if (++_carvedCells >= _script.carveCellThreshold) _latched.Add(TutorialGate.CarveComplete);
    }
    void OnTentacle()  => _latched.Add(TutorialGate.TentacleAppeared);
    void OnStarPoked() => _latched.Add(TutorialGate.StarPoked);
    void OnBurst()     => _latched.Add(TutorialGate.DiscoveryBurst);
    void OnPlaced()
    {
        _placedSinceCue++;
        _latched.Add(TutorialGate.NotePlaced);
    }
    void OnRing()      => _latched.Add(TutorialGate.RingCompleted);

    void OnStarArmed(Transform star)
    {
        _armedStar = star;
        // Call out the newly activated star unless the player is already past chasing it.
        if (!_latched.Contains(TutorialGate.StarPoked)) Pulse(star);
        _latched.Add(TutorialGate.StarArmed);
    }

    void OnFirstNoteSpawned(Collectable c)
    {
        _firstNote = c != null ? c.transform : null;
        if (!_latched.Contains(TutorialGate.NoteCaught)) Pulse(_firstNote);
    }

    void OnCaught(Collectable c)
    {
        _placeholder = c != null ? c.ribbonMarker : null;
        if (!_latched.Contains(TutorialGate.NotePlaced)) Pulse(_placeholder);
        _latched.Add(TutorialGate.NoteCaught);
    }

    void OnEscaped()
    {
        // The discovery is gone: a retry needs a fresh poke and burst. Once a note has been
        // caught the chase is truly over, so those latches are kept.
        if (!_latched.Contains(TutorialGate.NoteCaught))
        {
            _latched.Remove(TutorialGate.StarPoked);
            _latched.Remove(TutorialGate.DiscoveryBurst);
        }
        if (!_playingInterruption && _pending == Interruption.None && IsChaseStage(_stage))
            _pending = Interruption.Escaped;
    }

    void OnCommitWithoutNote()
    {
        // Ignore presses right after a line appears so a double-press can't skip an unread line.
        if (Time.unscaledTime - _lineShownTime >= _script.skipMinLineSeconds) _skipRequested = true;
    }

    bool ConsumeSkip()
    {
        if (!_skipRequested) return false;
        _skipRequested = false;
        return true;
    }

    // Shows a line and restarts the skip clock; a press made while it faded never skips the next line.
    IEnumerator ShowLine(string text, bool eventDriven)
    {
        yield return _view.Show(text, eventDriven);
        _lineShownTime = Time.unscaledTime;
        _skipRequested = false;
    }

    void OnDiscarded()
    {
        if (!_playingInterruption && _pending == Interruption.None && IsPlacementStage(_stage))
            _pending = Interruption.Discarded;
    }

    // ---- stage helpers ------------------------------------------------------------------

    TutorialGate GateOf(int stage) =>
        (stage >= 0 && stage < _script.stages.Length) ? _script.stages[stage].gate : TutorialGate.None;

    bool IsChaseStage(int stage)
    {
        var g = GateOf(stage);
        return g == TutorialGate.StarPoked || g == TutorialGate.DiscoveryBurst;
    }

    bool IsPlacementStage(int stage)
    {
        var g = GateOf(stage);
        return g == TutorialGate.NoteCaught || g == TutorialGate.NotePlaced || g == TutorialGate.RingCompleted;
    }

    /// <summary>The stage the player is actually on: one past the furthest stage whose gate is latched.</summary>
    int ComputeStage()
    {
        int stage = 0;
        for (int i = 0; i < _script.stages.Length; i++)
        {
            var g = _script.stages[i].gate;
            if (g != TutorialGate.None && _latched.Contains(g)) stage = i + 1;
        }
        return stage;
    }

    bool Superseded() => _pending != Interruption.None || ComputeStage() > _stage;

    void Pulse(Transform target)
    {
        if (target == null || _script == null) return;
        TutorialHighlightRing.Spawn(target, _script.highlightExpandSeconds, _script.highlightHoldSeconds,
                                    _script.highlightFadeSeconds, _script.highlightColor);
    }

    // Re-callout when the stage that talks about an object shows its instruction line.
    void PulseForGate(TutorialGate gate)
    {
        switch (gate)
        {
            case TutorialGate.StarArmed:
            case TutorialGate.StarPoked:  Pulse(_armedStar); break;
            case TutorialGate.NoteCaught: Pulse(_firstNote); break;
            case TutorialGate.NotePlaced: Pulse(_placeholder); break;
        }
    }

    // ---- sequencing ---------------------------------------------------------------------

    IEnumerator Run()
    {
        var stages = _script.stages;
        _stage = ComputeStage();
        int line = 0;
        bool eventDriven = false;

        while (_stage < stages.Length)
        {
            var st = stages[_stage];
            if (st.lines == null || st.lines.Length == 0) { _stage++; continue; }
            int last = st.lines.Length - 1;
            line = Mathf.Clamp(line, 0, last);

            yield return ShowLine(st.lines[line], eventDriven);
            eventDriven = false;
            if (line == last) PulseForGate(st.gate);

            if (line == st.cueLineIndex && _cueCompletedStage != _stage)
            {
                // Instruction line that cycles Now!/Here! with the real release window while a
                // note is carried. Ends on placement, an interruption, or the stage being superseded.
                yield return CueLoop(st.lines[line]);
            }
            else if (line < last)
            {
                // Narration line: auto-advance on the hold interval, unless the player got ahead.
                yield return WaitUntilSuperseded(_script.holdSeconds);
            }
            else if (st.gate == TutorialGate.None)
            {
                // Final instruction (stage 09): stays up until an interruption or the record completes.
                while (_pending == Interruption.None) yield return null;
            }
            else
            {
                // Final line holds until its gate fires (or the player is already further along).
                while (!Superseded()) yield return null;
            }

            if (_pending != Interruption.None)
            {
                yield return PlayInterruption();
                _stage = ComputeStage();   // resume the instruction the player actually needs now
                // Resume at the cue line if that stage has one still in progress, else its final line.
                int cue = _stage < stages.Length ? stages[_stage].cueLineIndex : -1;
                line = (cue >= 0 && _cueCompletedStage != _stage) ? cue : int.MaxValue;
                eventDriven = true;
                continue;
            }

            int next = ComputeStage();
            if (next > _stage)
            {
                // Objective done (possibly several): drop the rest of this stage's narration.
                _stage = next;
                line = 0;
                eventDriven = true;
                continue;
            }

            if (line < last) line++;
        }
    }

    // Waits up to `seconds`, ending early if an interruption is queued or the player moved on.
    IEnumerator WaitUntilSuperseded(float seconds)
    {
        float t = 0f;
        while (t < seconds && !Superseded() && !ConsumeSkip())
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    // Cycles the instruction line with "Now!" / "Here!" synchronized to the real note-release window.
    IEnumerator CueLoop(string lineText)
    {
        _placedSinceCue = 0;
        var words = _script.cueWindowTexts;
        int i = 0;
        bool first = true;

        while (!Superseded() && _placedSinceCue == 0)
        {
            // Dwell on the instruction (longer the first time so it can be read).
            float dwell = first ? _script.cueMinLineSeconds : _script.cueRepeatLineSeconds;
            for (float t = 0f; t < dwell && !Superseded() && _placedSinceCue == 0 && !ConsumeSkip(); t += Time.unscaledDeltaTime)
                yield return null;

            // Wait for the window to open (only happens while a note is being carried).
            Vehicle.ReleaseWindowState ws = default;
            while (!Superseded() && _placedSinceCue == 0 && !TryFindOpenWindow(out ws))
                yield return null;
            if (Superseded() || _placedSinceCue > 0) break;

            if (words == null || words.Length == 0) break;
            string word = words[i % words.Length];
            yield return ShowLine(word, true);
            if (i % words.Length == 1) PulseTargetPlaceholder(ws);

            // Hold until the window closes (or the note is placed / cap reached).
            float held = 0f;
            while (!Superseded() && _placedSinceCue == 0 && held < _script.cueMaxWindowSeconds && TryFindOpenWindow(out _))
            {
                held += Time.unscaledDeltaTime;
                yield return null;
            }

            // Fade the cue word out by returning to the instruction line.
            if (!Superseded() && _placedSinceCue == 0)
                yield return ShowLine(lineText, false);

            i++;
            first = false;
        }

        if (_placedSinceCue > 0) _cueCompletedStage = _stage;
    }

    bool TryFindOpenWindow(out Vehicle.ReleaseWindowState state)
    {
        if (Time.unscaledTime - _vehicleCacheTime > 0.5f)
        {
            _vehicleCache = FindObjectsByType<Vehicle>(FindObjectsSortMode.None);
            _vehicleCacheTime = Time.unscaledTime;
        }
        for (int i = 0; i < _vehicleCache.Length; i++)
        {
            var v = _vehicleCache[i];
            if (v == null) continue;
            if (v.TryGetReleaseWindowState(out state) && state.hasPendingNote && state.isOpen) return true;
        }
        state = default;
        return false;
    }

    void PulseTargetPlaceholder(Vehicle.ReleaseWindowState ws)
    {
        var viz = GameFlowManager.Instance != null ? GameFlowManager.Instance.noteViz : null;
        if (viz == null || ws.track == null) return;
        if (viz.noteMarkers.TryGetValue((ws.track, ws.targetAbsStep), out var marker) && marker != null)
            Pulse(marker);
    }

    IEnumerator PlayInterruption()
    {
        var kind = _pending;
        _pending = Interruption.None;

        var seq = kind == Interruption.Escaped ? _script.discoveryEscaped : _script.noteDiscarded;
        _playingInterruption = true;
        if (seq?.lines != null)
        {
            for (int i = 0; i < seq.lines.Length; i++)
            {
                yield return ShowLine(seq.lines[i], true);
                for (float t = 0f; t < _script.holdSeconds && !ConsumeSkip(); t += Time.unscaledDeltaTime)
                    yield return null;
            }
        }
        _playingInterruption = false;
        _pending = Interruption.None;
    }
}
