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

    enum Interruption { None, Escaped, Discarded, OutOfEnergy }

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
    Vehicle _lastCatcher;   // decides the copy variant on mixed Cozy Co-Pilot / Groove Commander tables
    Vehicle[] _vehicleCache = System.Array.Empty<Vehicle>();
    float _vehicleCacheTime = -10f;

    // Record spotlight driven by stage lines (TutorialStage.spotlightLine / spotlightEndLine).
    int _spotlightId;
    int _spotlightStage = -1;
    const float SpotlightSettleTimeout = 2.5f;

    // The star whose buds stage 03 ("These are discoveries.") lights up.
    PhaseStar _budStar;
    bool _budHighlightOn;

    static NarratedTutorialController _instance;

    /// <summary>
    /// The auto-started Phase 0 tutorial is in charge of the motif-start record zoom (its own lines
    /// zoom in and out), so GameFlowManager.IntroduceRecord skips its timed zoom.
    /// </summary>
    public static bool WillDriveIntroZoom
    {
        get
        {
            if (_instance == null || _instance._manualStart || !_instance.enabled) return false;
            var ptm = GameFlowManager.Instance != null ? GameFlowManager.Instance.phaseTransitionManager : null;
            return ptm != null && ptm.CurrentPhaseIndex == 0;
        }
    }

    /// <summary>
    /// True while the narrated tutorial is running: an on-demand start (pause menu), or the
    /// auto-start on the Phase 0 motif. Record camera zooms (GameFlowManager.RecordRecovery) only
    /// play while this is true; normal play keeps the camera home with nothing dimmed.
    /// </summary>
    public static bool IsRunning
    {
        get
        {
            var t = _instance;
            if (t == null || !t.enabled) return false;
            if (t._manualStart) return true;
            var ptm = GameFlowManager.Instance != null ? GameFlowManager.Instance.phaseTransitionManager : null;
            return ptm != null && ptm.CurrentPhaseIndex == 0;
        }
    }

    /// <summary>
    /// A stage still ahead (or current) zooms on the record from its own lines (spotlightLine, e.g.
    /// "You've completed your first ring!") and hasn't yet. Until it has, GameFlowManager's per-ring
    /// zoom stays out of the way, so the camera follows the narration rather than two competing
    /// clocks (note landing vs. holdSeconds).
    /// </summary>
    public static bool ClaimsRingSpotlight
    {
        get
        {
            var t = _instance;
            if (t == null || !t.enabled || t._script == null || t._script.stages == null) return false;
            var stages = t._script.stages;
            for (int i = Mathf.Max(0, t._stage); i < stages.Length; i++)
                if (stages[i].spotlightLine >= 0 && t._spotlightStage != i) return true;
            return false;
        }
    }

    void Awake() => _instance = this;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != GameplayScene) return;
        if (FindAnyObjectByType<NarratedTutorialController>() != null) return;
        new GameObject("NarratedTutorialController").AddComponent<NarratedTutorialController>();
    }

    void OnIntroRecordHidden() => _introHidden = true;

    // True when started on demand (pause menu): skips the Phase 0 restriction and the intro-ring wait.
    bool _manualStart;

    /// <summary>
    /// Restarts the tutorial from the first stage on whatever motif is playing. Replaces any running
    /// instance; latches start empty so every gate must be earned again.
    /// </summary>
    public static NarratedTutorialController RunNow()
    {
        var existing = FindAnyObjectByType<NarratedTutorialController>();
        if (existing != null)
        {
            existing.StopAllCoroutines();
            existing.enabled = false;
            Destroy(existing.gameObject);
        }
        var go = new GameObject("NarratedTutorialController");
        var c = go.AddComponent<NarratedTutorialController>();
        c._manualStart = true;
        return c;
    }

    IEnumerator Start()
    {
        // Subscribe immediately: the rings can finish fading while we are still loading below.
        TutorialEvents.IntroRecordHidden += OnIntroRecordHidden;

        // Auto-start only runs on the Phase 0 tutorial motif. Wait for the phase manager to apply the motif.
        // An on-demand start (pause menu) runs on any motif.
        if (!_manualStart)
        {
            // The control sandbox runs first in a new session (phase 0 is already resolved
            // under it); its own narration owns the panel until the vehicles take its path.
            while (GameFlowManager.SandboxActive) yield return null;

            PhaseTransitionManager ptm = null;
            float waited = 0f;
            while (waited < 15f)
            {
                if (ptm == null) ptm = FindAnyObjectByType<PhaseTransitionManager>();
                if (ptm != null && ptm.CurrentPhaseIndex >= 0) break;
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (ptm == null || ptm.CurrentPhaseIndex != 0) { Destroy(gameObject); yield break; }
        }

        _script = NarratedTutorialScript.Load();
        if (_script == null) { Destroy(gameObject); yield break; } // Load() already logged why

        _view = FindAuthoredView() ?? TutorialPromptView.Create(transform);
        Subscribe();
        _gfm = GameFlowManager.Instance;
        if (_gfm != null) _gfm.OnMotifCompleted += HandleRecordComplete;

        if (!_manualStart)
        {
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
        }

        yield return Run();
    }

    // Binds to the authored panel (e.g. Canvas/UI/Tutorial + its TMP child), without editing the scene.
    TutorialPromptView FindAuthoredView() => TutorialPromptView.BindAuthored(_script.panelPath, "NarratedTutorial");

    void OnDestroy()
    {
        if (_instance == this) _instance = null;
        EndSpotlight();
        SetBudHighlight(false);
        if (_view != null) _view.Deactivate();
        TutorialEvents.IntroRecordHidden -= OnIntroRecordHidden;
        Unsubscribe();
        TutorialHighlightRing.CancelAll();
        if (_gfm != null) _gfm.OnMotifCompleted -= HandleRecordComplete;
    }

    void HandleRecordComplete()
    {
        // The record was collected: Run is finishing its last line over the bridge and
        // cleans itself up when it ends.
        if (_latched.Contains(TutorialGate.RecordCollected)) return;
        // Record finished: the tutorial's job is done. Existing flow owns completion/saving.
        StopAllCoroutines();
        EndSpotlight();
        SetBudHighlight(false);
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
        TutorialEvents.NoteSounded         += OnNoteSounded;
        TutorialEvents.NoteCaught          += OnCaught;
        TutorialEvents.NotePlaced          += OnPlaced;
        TutorialEvents.RingCompleted       += OnRing;
        TutorialEvents.DiscoveryEscaped    += OnEscaped;
        TutorialEvents.NoteDiscarded       += OnDiscarded;
        TutorialEvents.CommitPressedWithoutNote += OnCommitWithoutNote;
        TutorialEvents.OutOfEnergyRescued  += OnOutOfEnergyRescued;
        TutorialEvents.RecordAccessible    += OnRecordAccessible;
        TutorialEvents.RecordCollected     += OnRecordCollected;
    }

    void Unsubscribe()
    {
        TutorialEvents.CellCarvedByVehicle -= OnCarved;
        TutorialEvents.TentacleAppeared    -= OnTentacle;
        TutorialEvents.StarArmed           -= OnStarArmed;
        TutorialEvents.StarPoked           -= OnStarPoked;
        TutorialEvents.DiscoveryBurst      -= OnBurst;
        TutorialEvents.FirstNoteSpawned    -= OnFirstNoteSpawned;
        TutorialEvents.NoteSounded         -= OnNoteSounded;
        TutorialEvents.NoteCaught          -= OnCaught;
        TutorialEvents.NotePlaced          -= OnPlaced;
        TutorialEvents.RingCompleted       -= OnRing;
        TutorialEvents.DiscoveryEscaped    -= OnEscaped;
        TutorialEvents.NoteDiscarded       -= OnDiscarded;
        TutorialEvents.CommitPressedWithoutNote -= OnCommitWithoutNote;
        TutorialEvents.OutOfEnergyRescued  -= OnOutOfEnergyRescued;
        TutorialEvents.RecordAccessible    -= OnRecordAccessible;
        TutorialEvents.RecordCollected     -= OnRecordCollected;
    }

    void OnCarved()
    {
        if (++_carvedCells >= _script.carveCellThreshold) _latched.Add(TutorialGate.CarveComplete);
    }
    void OnTentacle(Transform star)
    {
        if (star != null)
        {
            var ps = star.GetComponentInParent<PhaseStar>();
            if (ps != null && ps != _budStar)
            {
                bool wasOn = _budHighlightOn;
                SetBudHighlight(false);
                _budStar = ps;
                if (wasOn) SetBudHighlight(true);
            }
        }
        _latched.Add(TutorialGate.TentacleAppeared);
    }
    void OnStarPoked() => _latched.Add(TutorialGate.StarPoked);
    void OnBurst()     => _latched.Add(TutorialGate.DiscoveryBurst);
    void OnPlaced()
    {
        _placedSinceCue++;
        _latched.Add(TutorialGate.NotePlaced);
    }
    void OnRing()      => _latched.Add(TutorialGate.RingCompleted);
    void OnRecordAccessible() => _latched.Add(TutorialGate.RecordAccessible);
    void OnRecordCollected()  => _latched.Add(TutorialGate.RecordCollected);

    void OnStarArmed(Transform star)
    {
        _armedStar = star;
        // Call out the newly activated star unless the player is already past chasing it.
        if (!_latched.Contains(TutorialGate.StarPoked)) Pulse(star);
        _latched.Add(TutorialGate.StarArmed);
    }

    void OnFirstNoteSpawned(Collectable c)
    {
        // Remember it only; the highlight itself is driven by OnNoteSounded so it appears when
        // the note actually plays rather than the instant it spawns.
        _firstNote = c != null ? c.transform : null;
    }

    // While the player is on the "Can you hear it?" stage, the highlight follows whichever
    // note is sounding as it launches, so it matches what they hear.
    void OnNoteSounded(Collectable c)
    {
        if (c == null || _script == null) return;
        if (_latched.Contains(TutorialGate.NoteCaught)) return;
        if (GateOf(ComputeStage()) != TutorialGate.NoteCaught) return;
        _firstNote = c.transform;
        TutorialHighlightRing.CancelAll();   // one ring at a time: the highlight moves with the sound
        Pulse(_firstNote);
    }

    void OnCaught(Collectable c, Vehicle catcher)
    {
        if (catcher != null) _lastCatcher = catcher;
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

    // Any stage, and it outranks the other interruptions: a near-death is the more important lesson.
    // If another interruption is mid-playback this queues and plays right after it.
    void OnOutOfEnergyRescued(Vehicle v) => _pending = Interruption.OutOfEnergy;

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

    // "{s}" is a plural suffix: "Wanderer{s}" -> "Wanderers" with 2+ players. Read at show time
    // because players can still be joining/leaving when the tutorial starts.
    static string ExpandTokens(string text) => TutorialPromptView.ExpandTokens(text);

    // Shows a line and restarts the skip clock; a press made while it faded never skips the next line.
    IEnumerator ShowLine(string text, bool eventDriven)
    {
        yield return _view.Show(ExpandTokens(text), eventDriven);
        _lineShownTime = Time.unscaledTime;
        _skipRequested = false;
    }

    void OnDiscarded()
    {
        if (!_playingInterruption && _pending == Interruption.None && IsPlacementStage(_stage))
            _pending = Interruption.Discarded;
    }

    // ---- stage helpers ------------------------------------------------------------------

    // Cozy Co-Pilot = Auto placement; Groove Commander = Manual. All Auto -> auto copy, all Manual -> original,
    // mixed -> whichever mode the last note-catching vehicle uses (original until someone has caught a note).
    bool UseAutoCopy()
    {
        var vehicles = GameFlowManager.Instance != null ? GameFlowManager.Instance.GetVehicles() : null;
        if (vehicles == null || vehicles.Count == 0) return false;
        bool anyAuto = false, anyManual = false;
        for (int i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            if (v == null) continue;
            if (v.PlacementMethod == NotePlacementMethod.Auto) anyAuto = true; else anyManual = true;
        }
        if (anyAuto && !anyManual) return true;
        if (!anyAuto) return false;
        return _lastCatcher != null && _lastCatcher.PlacementMethod == NotePlacementMethod.Auto;
    }

    bool HasAutoVariant(TutorialStage st) => st.linesAuto != null && st.linesAuto.Length > 0 && UseAutoCopy();
    string[] LinesFor(TutorialStage st) => HasAutoVariant(st) ? st.linesAuto : st.lines;
    int CueIndexFor(TutorialStage st) => HasAutoVariant(st) ? st.cueLineIndexAuto : st.cueLineIndex;

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

    // Narration waits: a stage advance only replaces the current line once it has been readable
    // for minLineSeconds, so fast gates can't flash lines past. Interruptions still cut through.
    bool LineSuperseded() =>
        _pending != Interruption.None ||
        (ComputeStage() > _stage && Time.unscaledTime - _lineShownTime >= _script.minLineSeconds);

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
            var stLines = LinesFor(st);
            if (stLines == null || stLines.Length == 0) { _stage++; continue; }
            int last = stLines.Length - 1;
            line = Mathf.Clamp(line, 0, last);
            int stCue = CueIndexFor(st);
            // Cozy Co-Pilot copy narrates what the co-pilot is doing; its gates fire on their own,
            // so the lines play through instead of being dropped when the gate latches early.
            bool finishLines = HasAutoVariant(st);

            // Record spotlight: end it once its line range is over; zoom in (and wait for the camera
            // to arrive) before the line that starts it.
            if (_spotlightId != 0 && (_spotlightStage != _stage || line < st.spotlightLine ||
                                      (st.spotlightEndLine >= 0 && line >= st.spotlightEndLine)))
                EndSpotlight();
            if (line == st.spotlightLine && _spotlightId == 0 && _spotlightStage != _stage)
            {
                yield return BeginSpotlight();
                if (Superseded())
                {
                    // Moved on while the camera travelled: drop the zoom and re-evaluate.
                    EndSpotlight();
                    int ahead = ComputeStage();
                    if (ahead > _stage)
                    {
                        _stage = ahead;
                        line = 0;
                        eventDriven = false;
                        continue;
                    }
                    // An interruption: play it, then resume like the main interruption path
                    // (the cue line if still in progress, else the stage's final line).
                    yield return PlayInterruption();
                    _stage = ComputeStage();
                    int resumeCue = _stage < stages.Length ? CueIndexFor(stages[_stage]) : -1;
                    line = (resumeCue >= 0 && _cueCompletedStage != _stage) ? resumeCue : int.MaxValue;
                    eventDriven = true;
                    continue;
                }
            }

            // "These are discoveries.": the star's buds glow while that stage is up.
            SetBudHighlight(st.gate == TutorialGate.StarArmed);

            yield return ShowLine(stLines[line], eventDriven);
            eventDriven = false;
            if (line == last) PulseForGate(st.gate);
            // "These are discoveries.": ring the whole tendril mass as the line appears.
            if (line == 0 && st.gate == TutorialGate.StarArmed) PulseTendrils();

            // Line first, then the camera visits the record and comes home; that visit is the
            // line's hold ("There's a piece of music trapped here.").
            bool visited = false;
            if (line == st.spotlightDuringLine && _spotlightStage != _stage)
            {
                yield return SpotlightVisit();
                visited = true;
            }

            if (line == stCue && _cueCompletedStage != _stage)
            {
                // Instruction line that cycles Now!/Here! with the real release window while a
                // note is carried. Ends on placement, an interruption, or the stage being superseded.
                yield return CueLoop(stLines[line]);
            }
            else if (line < last)
            {
                // Narration line: auto-advance on the hold interval, unless the player got ahead.
                // After a spotlight visit the line has already been up for the whole zoom.
                yield return WaitUntilSuperseded(visited ? 0.5f : _script.holdSeconds, ignoreAdvance: finishLines);
            }
            else if (st.gate == TutorialGate.None)
            {
                // Final stage: stays up until an interruption or the record completes. Once the
                // record has been collected (the closing "listen" beat, playing over the bridge), or
                // for a replay started from the pause menu, it holds one interval, fades out, and finishes.
                if (_manualStart || _latched.Contains(TutorialGate.RecordCollected))
                {
                    for (float t = 0f; t < _script.holdSeconds && _pending == Interruption.None; t += Time.unscaledDeltaTime)
                        yield return null;
                    if (_pending == Interruption.None)
                    {
                        yield return _view.Hide();
                        Destroy(gameObject);
                        yield break;
                    }
                }
                else
                    while (_pending == Interruption.None) yield return null;
            }
            else
            {
                // Final line holds until its gate fires (or the player is already further along).
                while (!LineSuperseded()) yield return null;
            }

            if (_pending != Interruption.None)
            {
                EndSpotlight();
                yield return PlayInterruption();
                _stage = ComputeStage();   // resume the instruction the player actually needs now
                // Resume at the cue line if that stage has one still in progress, else its final line.
                int cue = _stage < stages.Length ? CueIndexFor(stages[_stage]) : -1;
                line = (cue >= 0 && _cueCompletedStage != _stage) ? cue : int.MaxValue;
                eventDriven = true;
                continue;
            }

            int next = ComputeStage();
            if (next > _stage && finishLines && line < last)
            {
                line++;   // co-pilot copy: finish this stage's lines before moving on
                continue;
            }
            if (next > _stage)
            {
                // Objective done (possibly several): drop the rest of this stage's narration.
                // The new prompt fades in at the normal pace so the change reads as an advance.
                _stage = next;
                line = 0;
                eventDriven = false;
                continue;
            }

            if (line < last) line++;
        }

        // Every gate is done (record collected): let the last line be read, fade out, finish.
        for (float t = 0f; t < _script.minLineSeconds; t += Time.unscaledDeltaTime)
            yield return null;
        yield return _view.Hide();
        Destroy(gameObject);
    }

    // Waits up to `seconds`, ending early on a commit-skip, a queued interruption, or (unless
    // ignoreAdvance) the player having moved on once the line has been readable for minLineSeconds.
    IEnumerator WaitUntilSuperseded(float seconds, bool ignoreAdvance = false)
    {
        float t = 0f;
        while (t < seconds && !ConsumeSkip() &&
               !(ignoreAdvance ? _pending != Interruption.None : LineSuperseded()))
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
            _vehicleCache = FindObjectsByType<Vehicle>();
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

    // Zooms in on the docked record and waits (capped) for the camera to arrive.
    IEnumerator BeginSpotlight()
    {
        _spotlightStage = _stage; // once per stage, even if there is nothing to show
        var gfm = GameFlowManager.Instance;
        if (gfm == null) yield break;
        _spotlightId = gfm.BeginNarrationSpotlight();
        if (_spotlightId == 0) yield break;
        // Let the zoom start moving before testing for arrival (it reads settled at rest).
        yield return null;
        yield return null;
        for (float t = 0f; t < SpotlightSettleTimeout && !gfm.IsSpotlightSettled && !Superseded(); t += Time.unscaledDeltaTime)
            yield return null;
    }

    // With the line up: wait a beat, zoom in on the record, hold, pull back, and wait until the
    // camera is home. Ends early (releasing the zoom) if the player moves on or an interruption queues.
    IEnumerator SpotlightVisit()
    {
        for (float t = 0f; t < _script.spotlightLeadSeconds && !Superseded(); t += Time.unscaledDeltaTime)
            yield return null;
        if (Superseded()) { _spotlightStage = _stage; yield break; }

        yield return BeginSpotlight();
        if (_spotlightId != 0)
            for (float t = 0f; t < _script.spotlightHoldSeconds && !Superseded(); t += Time.unscaledDeltaTime)
                yield return null;
        EndSpotlight();

        var gfm = GameFlowManager.Instance;
        for (float t = 0f; gfm != null && t < SpotlightSettleTimeout && !gfm.IsCameraHome && !Superseded(); t += Time.unscaledDeltaTime)
            yield return null;
    }

    void EndSpotlight()
    {
        if (_spotlightId == 0) return;
        var gfm = GameFlowManager.Instance;
        if (gfm != null) gfm.EndNarrationSpotlight(_spotlightId);
        _spotlightId = 0;
    }

    // The star has no sprite body yet (it is all tendrils), so size the ring from their reach.
    void PulseTendrils()
    {
        if (_budStar == null || _script == null) return;
        TutorialHighlightRing.Spawn(_budStar.transform, _script.highlightExpandSeconds, _script.highlightHoldSeconds,
                                    _script.highlightFadeSeconds, _script.highlightColor,
                                    minRadius: _budStar.TendrilReachWorld * 1.55f);
    }

    void SetBudHighlight(bool on)
    {
        if (_budHighlightOn == on) return;
        _budHighlightOn = on;
        if (_budStar != null) _budStar.SetBudHighlight(on);
    }

    IEnumerator PlayInterruption()
    {
        var kind = _pending;
        _pending = Interruption.None;

        var seq = kind switch
        {
            Interruption.Escaped     => _script.discoveryEscaped,
            Interruption.OutOfEnergy => _script.outOfEnergy,
            _                        => _script.noteDiscarded,
        };
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
        // _pending is not cleared here: it was cleared on entry, and anything set since (only a
        // rescue can be, the others ignore events during playback) should play next.
    }
}
