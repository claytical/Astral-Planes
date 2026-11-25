using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pre-motif control sandbox (GeneratedTrack, every new session). Players are in their vehicles
/// on an empty field; the Control Scheme panel fades each command in centred, holds until the
/// input is detected, fades it out, and after a short gap fades in the next:
///   Drift  -> fly around.
///   Charge -> boost; role-coloured dust patches grow in (soft -> hard) and players carve each.
/// Narration runs in the same lower-third panel as the narrated tutorial
/// (Resources/Tutorial/ControlSandbox.json). No PhaseStar exists here, so no tentacles; carved
/// sandbox dust never regrows (VoidGrow cells drop their imprint on a vehicle carve).
///
/// Run() returns once the lesson is done (or skipped); SceneFlowCoordinator then opens the single
/// rainbow road into the session's motif. Returning players (any ring saved) can hold South to skip.
/// </summary>
public class ControlSandboxDirector : MonoBehaviour
{
    const string ScriptPath = "Tutorial/ControlSandbox"; // Assets/Resources/Tutorial/ControlSandbox.json

    const string StageWelcome  = "01_welcome";
    const string StageDrift    = "02_drift";
    const string StageCharge   = "03_charge";
    const string StageDust     = "04_dust";
    const string StageWrapUp   = "05_wrapUp";

    enum Pending { None, OutOfFuel, Stuck }

    GameFlowManager _gfm;
    MotifProfile _motif;
    readonly List<LocalPlayer> _players = new();

    ControlSandboxConfig _cfg;
    NarratedTutorialScript _script;
    TutorialPromptView _view;
    ControlTutorialHighlight _panel;

    // Dust lesson
    readonly List<MusicalRole> _roles = new();
    readonly Dictionary<MusicalRole, int> _carvedByRole = new();
    readonly Dictionary<MusicalRole, int> _neededByRole = new();
    readonly Dictionary<MusicalRole, List<Vector2Int>> _patchCells = new();
    readonly HashSet<MusicalRole> _doneRoles = new();
    bool _dustLessonActive;
    float _chargeWithoutCarve;
    bool _stuckHintShown;

    // Narration
    Pending _pending;
    string _currentLine;

    // Flow
    bool _lessonDone;
    bool _skipped;
    bool _canSkip;
    float _skipHeld;
    bool _skipHoldUiShown;

    public static ControlSandboxDirector Create(GameFlowManager gfm, IReadOnlyList<LocalPlayer> players, MotifProfile motif)
    {
        var go = new GameObject("ControlSandboxDirector");
        var d = go.AddComponent<ControlSandboxDirector>();
        d._gfm = gfm;
        d._motif = motif;
        if (players != null)
            foreach (var p in players)
                if (p) d._players.Add(p);
        return d;
    }

    /// <summary>Runs the lesson; returns when it is finished or skipped. Yield on it from the setup coroutine.</summary>
    public IEnumerator Run()
    {
        _cfg = ControlSandboxConfig.LoadOrDefault();
        _script = NarratedTutorialScript.Load(ScriptPath);
        if (_script != null)
            _view = TutorialPromptView.BindAuthored(_script.panelPath, "ControlSandbox") ?? TutorialPromptView.Create(transform);

        var ctd = ControlTutorialDirector.Instance;
        if (ctd != null)
            _panel = ctd.BeginSandboxPanel(_players);
        if (_panel) _panel.SetSandboxFades(_cfg.panelFadeInSeconds, _cfg.panelFadeOutSeconds);

        TutorialEvents.RoleCellCarvedByVehicle += OnRoleCarved;
        TutorialEvents.OutOfEnergyRescued      += OnFuelRescued;

        // Returning players (the tutorial ring, or anything past it, is saved) may skip.
        _canSkip = RingSessionStore.LoadAllRingsFromDisk().Count > 0;

        var lesson = StartCoroutine(Lesson());
        while (!_lessonDone && !_skipped)
        {
            if (this == null) yield break; // scene unloaded under us (quit)
            if (_canSkip) TickSkip();
            yield return null;
        }

        if (_skipped)
        {
            if (lesson != null) StopCoroutine(lesson);
            _pending = Pending.None;
        }
        _dustLessonActive = false;
        EndSkipHoldUi(completed: _skipped);

        if (ctd != null) ctd.EndSandboxPanel(completed: !_skipped);

        // No interstitial road any more: the motif grows in right here once Run() returns.
        // Top everyone up now, close on a wrap-up line, and clear whatever lesson dust is left so
        // the maze's ClearMaze doesn't pop it.
        foreach (var p in _players)
            if (p && p.plane != null) p.plane.RecoverEnergy(p.plane.capacity);
        StartCoroutine(WrapUpNarration());
        yield return DissolveAllPatches();
    }

    // ---------------------------------------------------------------------------------------
    // Lesson
    // ---------------------------------------------------------------------------------------

    IEnumerator Lesson()
    {
        // A quiet beat so the first line doesn't land the instant the sandbox appears.
        yield return WaitSeconds(_cfg.introDelaySeconds);

        if (_canSkip && _script?.skipHint?.lines != null)
            yield return PlayLines(_script.skipHint.lines);

        yield return NarrateUntil(Stage(StageWelcome), 0, null);

        // ── Drift ─────────────────────────────────────────────────────────────
        var drift = Stage(StageDrift);
        float drifted = 0f;
        System.Func<bool> driftDone = () =>
        {
            if (AnyDrifting()) drifted += Time.unscaledDeltaTime;
            return drifted >= _cfg.driftPracticeSeconds;
        };
        yield return PresentCommand(drift, 0, driftDone);

        // ── Charge ────────────────────────────────────────────────────────────
        // Counts the trigger itself (not the boost), so an empty tank or a squeeze during the
        // centred presentation still shows the player has it.
        var charge = Stage(StageCharge);
        float charged = 0f;
        System.Func<bool> chargeDone = () =>
        {
            if (AnyCharging()) charged += Time.unscaledDeltaTime;
            return charged >= _cfg.chargePracticeSeconds;
        };
        yield return PresentCommand(charge, 1, chargeDone);
        // Both controls learned (the panel has faded out): the dust lesson runs without it.
        SpawnDustField();

        // ── Dust: colour = role, some soft, some hard ─────────────────────────
        _dustLessonActive = true;
        _chargeWithoutCarve = 0f;
        yield return NarrateUntil(Stage(StageDust), 0, AllRolesCarved);
        _dustLessonActive = false;

        // Lesson complete: confirm beat, then EndSandboxPanel tidies the (already hidden) panel.
        if (_panel) _panel.PlayStepConfirm();
        yield return WaitSeconds(Mathf.Max(_cfg.stepConfirmBeatSeconds, _panel ? _panel.StepConfirmSeconds : 0f));
        _lessonDone = true;
    }

    // Fades a command in centred and holds it while the player practises (practice counts from
    // the moment it appears). Once the input is detected: confirm ding, fade out, short gap.
    IEnumerator PresentCommand(TutorialStage st, int panelStep, System.Func<bool> practiced)
    {
        if (_panel) _panel.PresentSandboxStep(panelStep);
        yield return ShowStageLine(st, 0);
        yield return NarrateUntil(st, 1, practiced);

        if (_panel)
        {
            _panel.PlayStepConfirm();
            _panel.DismissSandboxStep();
        }
        yield return WaitSeconds(_cfg.panelFadeOutSeconds + _cfg.stepConfirmBeatSeconds);
    }

    // Closing line(s) while the motif grows in around the ships, then the panel fades out.
    IEnumerator WrapUpNarration()
    {
        if (_view == null) yield break;
        var st = Stage(StageWrapUp);
        if (st?.lines != null && !_skipped)
            for (int i = 0; i < st.lines.Length; i++)
            {
                yield return ShowLine(st.lines[i]);
                yield return WaitSeconds(_script != null ? _script.holdSeconds : 2f);
            }
        if (_view != null) yield return _view.Hide();
    }

    // ---------------------------------------------------------------------------------------
    // Narration helpers (same lower-third panel + pacing as NarratedTutorialController)
    // ---------------------------------------------------------------------------------------

    TutorialStage Stage(string id) => _script != null ? _script.StageById(id) : null;

    IEnumerator ShowLine(string text, bool eventDriven = false)
    {
        _currentLine = text;
        if (_view != null) yield return _view.Show(TutorialPromptView.ExpandTokens(text), eventDriven);
    }

    IEnumerator ShowStageLine(TutorialStage st, int index)
    {
        if (st?.lines == null || index < 0 || index >= st.lines.Length) yield break;
        yield return ShowLine(st.lines[index]);
    }

    IEnumerator PlayLines(string[] lines)
    {
        if (lines == null) yield break;
        for (int i = 0; i < lines.Length; i++)
        {
            yield return ShowLine(lines[i]);
            yield return WaitSeconds(_script != null ? _script.holdSeconds : 3f);
        }
    }

    /// <summary>
    /// Shows stage lines from <paramref name="startLine"/>, advancing every holdSeconds; the last
    /// line stays up until <paramref name="gate"/> is true (gate == null: play the lines and return).
    /// The gate is evaluated exactly once per frame. Interruptions play in between and the
    /// current line comes back after them.
    /// </summary>
    IEnumerator NarrateUntil(TutorialStage st, int startLine, System.Func<bool> gate)
    {
        string[] lines = st?.lines;
        int count = lines?.Length ?? 0;
        int i = startLine;
        float hold = _script != null ? _script.holdSeconds : 3f;

        if (i < count) yield return ShowLine(lines[i]);

        float t = 0f;
        while (true)
        {
            if (gate != null && gate()) yield break;
            if (gate == null && i >= count - 1 && t >= hold) yield break;

            if (_pending != Pending.None)
            {
                yield return PlayInterruption(gate);
                if (gate != null && gate()) yield break; // goal met mid-interruption: move on now
                if (_currentLine != null) yield return ShowLine(_currentLine, eventDriven: true);
                t = 0f;
                continue;
            }

            t += Time.unscaledDeltaTime;
            if (i < count - 1 && t >= hold)
            {
                i++;
                yield return ShowLine(lines[i]);
                t = 0f;
                continue;
            }
            yield return null;
        }
    }

    // Plays an interruption, but never holds the lesson hostage: the stage gate keeps being
    // evaluated every frame and the interruption ends the moment it's satisfied.
    IEnumerator PlayInterruption(System.Func<bool> gate)
    {
        var kind = _pending;
        _pending = Pending.None;
        var seq = kind == Pending.OutOfFuel ? _script?.outOfEnergy : _script?.stuckOnHardDust;
        string resume = _currentLine;
        if (seq?.lines != null)
            for (int i = 0; i < seq.lines.Length; i++)
            {
                if (gate != null && gate()) break;
                yield return ShowLine(seq.lines[i], eventDriven: true);
                for (float t = 0f; t < _script.holdSeconds; t += Time.unscaledDeltaTime)
                {
                    if (gate != null && gate()) { _currentLine = resume; yield break; }
                    yield return null;
                }
            }
        _currentLine = resume;
    }

    static IEnumerator WaitSeconds(float seconds)
    {
        for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            yield return null;
    }

    // ---------------------------------------------------------------------------------------
    // Input / gates
    // ---------------------------------------------------------------------------------------

    bool AnyDrifting()
    {
        foreach (var p in _players)
            if (p && p.plane != null && p.GetMoveInput().magnitude > _cfg.inputThreshold) return true;
        return false;
    }

    bool AnyCharging()
    {
        foreach (var p in _players)
            if (p && p.plane != null && p.plane.BoostRequested) return true;
        return false;
    }


    bool AllRolesCarved()
    {
        foreach (var role in _roles)
        {
            _carvedByRole.TryGetValue(role, out int got);
            _neededByRole.TryGetValue(role, out int need);
            if (got < need) return false;
        }
        return true;
    }

    void OnRoleCarved(MusicalRole role)
    {
        _chargeWithoutCarve = 0f;
        if (!_carvedByRole.TryGetValue(role, out int n)) return;
        _carvedByRole[role] = ++n;

        // In-world tell (no counters): once a colour has been carved enough, the rest of its
        // patch dissolves, so whatever dust is still standing is exactly what's left to do.
        if (_neededByRole.TryGetValue(role, out int need) && n >= need && _doneRoles.Add(role))
        {
            if (_panel) _panel.PlayStepConfirm();
            StartCoroutine(DissolvePatch(role));
        }
    }

    IEnumerator DissolvePatch(MusicalRole role)
    {
        var dust = _gfm != null ? _gfm.dustGenerator : null;
        if (dust == null || !_patchCells.TryGetValue(role, out var cells)) yield break;

        var order = new List<Vector2Int>(cells);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        float step = _cfg.patchDissolveSeconds / Mathf.Max(1, order.Count);
        foreach (var c in order)
        {
            if (dust == null) yield break;
            // CarveCell (not the vehicle chip path) never raises the carve events, and with
            // scheduleRegrow:false the cell stays clear. Non-solid cells are skipped inside.
            dust.CarveCell(c, 0.35f, scheduleRegrow: false, source: DustClearSource.VehiclePlow,
                regrowDelaySeconds: -1f, runPreExplode: true);
            if (step > 0f) yield return WaitSeconds(step);
        }
    }

    bool _fuelHintShown;

    // Once per sandbox: hard charging can empty the tank (and trigger the rescue) repeatedly.
    void OnFuelRescued(Vehicle v)
    {
        if (_fuelHintShown || _lessonDone) return;
        if (_script?.outOfEnergy?.lines == null || _script.outOfEnergy.lines.Length == 0) return;
        _fuelHintShown = true;
        if (_pending == Pending.None) _pending = Pending.OutOfFuel;
    }

    // Tanks are topped up the moment the road takes the vehicles (input locks for the carry),
    // so nobody starts the first motif on fumes from all the charging practice.
    // Every patch not already dissolved by meeting its quota dissolves together; returns when done.
    IEnumerator DissolveAllPatches()
    {
        var running = new List<Coroutine>();
        foreach (var role in _patchCells.Keys)
            if (_doneRoles.Add(role))
                running.Add(StartCoroutine(DissolvePatch(role)));
        foreach (var co in running)
            yield return co;
    }

    void Update()
    {
        if (!_dustLessonActive || _stuckHintShown || _cfg == null) return;
        if (!AnyCharging()) return;
        _chargeWithoutCarve += Time.unscaledDeltaTime;
        if (_chargeWithoutCarve >= _cfg.stuckHintSeconds &&
            _script?.stuckOnHardDust?.lines != null && _script.stuckOnHardDust.lines.Length > 0)
        {
            _stuckHintShown = true;
            if (_pending == Pending.None) _pending = Pending.Stuck;
        }
    }

    // Hold South (any player) to skip straight to the path. Uses the panel's hold bar.
    void TickSkip()
    {
        bool held = false;
        foreach (var p in _players)
            if (p && p.IsReleaseHeld()) { held = true; break; }

        var ctd = ControlTutorialDirector.Instance;
        if (!held)
        {
            if (_skipHoldUiShown) { ctd?.CancelAbortHoldUI(); _skipHoldUiShown = false; }
            _skipHeld = 0f;
            return;
        }

        if (!_skipHoldUiShown) { ctd?.BeginAbortHoldUI("Skipping", _cfg.skipHoldSeconds); _skipHoldUiShown = true; }
        _skipHeld += Time.unscaledDeltaTime;
        ctd?.UpdateAbortHoldUI(Mathf.Clamp01(_skipHeld / Mathf.Max(0.01f, _cfg.skipHoldSeconds)));
        if (_skipHeld >= _cfg.skipHoldSeconds) _skipped = true;
    }

    void EndSkipHoldUi(bool completed)
    {
        if (!_skipHoldUiShown) return;
        var ctd = ControlTutorialDirector.Instance;
        if (completed) ctd?.EndAbortHoldUI(); else ctd?.CancelAbortHoldUI();
        _skipHoldUiShown = false;
    }

    // ---------------------------------------------------------------------------------------
    // Dust field
    // ---------------------------------------------------------------------------------------

    void SpawnDustField()
    {
        var dust = _gfm != null ? _gfm.dustGenerator : null;
        var drums = _gfm != null ? _gfm.activeDrumTrack : null;
        if (dust == null || drums == null) return;

        PickRoles();
        int k = _roles.Count;
        if (k == 0) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        int r = Mathf.Max(1, _cfg.patchRadiusCells);
        int clear = Mathf.Max(0, _cfg.vehicleClearRadiusCells);

        var vehicleCells = new List<Vector2Int>();
        foreach (var p in _players)
            if (p && p.plane != null) vehicleCells.Add(drums.WorldToGridPosition(p.plane.transform.position));

        int cy = Mathf.Clamp(Mathf.RoundToInt(h * _cfg.patchRowFraction), r, Mathf.Max(r, h - 1 - r));
        for (int i = 0; i < k; i++)
        {
            var role = _roles[i];
            int cx = Mathf.RoundToInt(w * (i + 1) / (float)(k + 1));
            var center = new Vector2Int(cx, cy);

            var cells = new List<Vector2Int>();
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var c = new Vector2Int(center.x + dx, center.y + dy);
                if (c.x < 0 || c.y < 0 || c.x >= w || c.y >= h) continue;
                bool nearVehicle = false;
                foreach (var vc in vehicleCells)
                    if ((vc - c).sqrMagnitude <= clear * clear) { nearVehicle = true; break; }
                if (!nearVehicle) cells.Add(c);
            }
            if (cells.Count == 0) continue;

            var profile = ResolveProfile(role);
            Color hue = profile != null ? profile.GetBaseColor() : Color.white;
            dust.SpawnDustAtCells(cells, role, hue, energy01: 1f, growInSeconds: _cfg.dustGrowInSeconds,
                solid: false, bloomed: true);

            _carvedByRole[role] = 0;
            _neededByRole[role] = Mathf.Clamp(_cfg.cellsPerRole, 1, cells.Count);
            _patchCells[role] = cells;
        }

        // Only roles that actually got a patch gate the lesson.
        _roles.RemoveAll(role => !_neededByRole.ContainsKey(role));
    }

    // maxRoles colours spread soft -> hard. The destination motif's own roles come first (so the
    // colours preview what players meet next); a motif with fewer roles (the Phase 0 tutorial
    // has one) is padded from the other roles, each pick the one whose hardness differs most
    // from those already chosen, so "every color feels a little different" is true on screen.
    void PickRoles()
    {
        _roles.Clear();
        int max = Mathf.Clamp(_cfg.maxRoles, 1, 5);

        var required = new List<(MusicalRole role, float res)>();
        var source = _motif != null ? _motif.GetActiveRoles() : null;
        if (source != null)
            foreach (var role in source)
            {
                if (role == MusicalRole.None || required.Exists(c => c.role == role)) continue;
                var prof = ResolveProfile(role);
                if (prof != null) required.Add((role, prof.carveResistance01));
            }
        required.Sort((a, b) => a.res.CompareTo(b.res));

        var picked = new List<(MusicalRole role, float res)>();
        if (required.Count > max)
        {
            // More motif roles than slots: thin them evenly across the soft -> hard range.
            int n = required.Count;
            if (max == 1) picked.Add(required[0]);
            else
                for (int j = 0; j < max; j++)
                {
                    var c = required[Mathf.RoundToInt(j * (n - 1) / (float)(max - 1))];
                    if (!picked.Exists(p => p.role == c.role)) picked.Add(c);
                }
        }
        else
        {
            picked.AddRange(required);

            var pool = new List<(MusicalRole role, float res)>();
            foreach (MusicalRole role in System.Enum.GetValues(typeof(MusicalRole)))
            {
                if (role == MusicalRole.None || picked.Exists(c => c.role == role)) continue;
                var prof = ResolveProfile(role);
                if (prof != null) pool.Add((role, prof.carveResistance01));
            }

            while (picked.Count < max && pool.Count > 0)
            {
                int best = 0;
                float bestGap = -1f;
                for (int i = 0; i < pool.Count; i++)
                {
                    float gap = float.MaxValue;
                    foreach (var c in picked) gap = Mathf.Min(gap, Mathf.Abs(c.res - pool[i].res));
                    if (picked.Count == 0) gap = pool[i].res; // nothing yet: start from the hardest
                    if (gap > bestGap) { bestGap = gap; best = i; }
                }
                picked.Add(pool[best]);
                pool.RemoveAt(best);
            }
        }

        picked.Sort((a, b) => a.res.CompareTo(b.res)); // patches lay out soft (left) -> hard (right)
        foreach (var c in picked) _roles.Add(c.role);
    }

    MusicalRoleProfile ResolveProfile(MusicalRole role)
    {
        MusicalRoleProfile p = _motif != null ? _motif.GetInstrumentProfileForVoice(role, 0) : null;
        return p != null ? p : MusicalRoleProfileLibrary.GetProfile(role);
    }

    // ---------------------------------------------------------------------------------------

    void OnDestroy()
    {
        TutorialEvents.RoleCellCarvedByVehicle -= OnRoleCarved;
        TutorialEvents.OutOfEnergyRescued      -= OnFuelRescued;
        if (_view != null) _view.Deactivate();

        // Quit / scene unload mid-sandbox: never leave the session flagged as in the sandbox.
        GameFlowManager.SandboxActive = false;
        if (!_lessonDone && !_skipped) ControlTutorialDirector.Instance?.EndSandboxPanel(completed: false);
    }
}
