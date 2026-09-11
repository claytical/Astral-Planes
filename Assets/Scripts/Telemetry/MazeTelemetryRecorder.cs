using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Greater Maze telemetry: records each player's ordered Phase -> Motif -> Motif path for a run,
// plus the exact committed musical solution for every completed motif. Fully separate from
// SteamStatsManager — this only subscribes to existing events, never alters their firing
// semantics, and shares no code with the Steam stats funnel.
public class MazeTelemetryRecorder : MonoBehaviour
{
    [SerializeField] private MazeTelemetryUploader uploader;

    private bool _sessionWired;
    private bool _motifHooksWired;
    private bool _runActive;
    private int _currentVisitOrder = -1;
    private MazeRunRecord _current;

    private void Update() => HooksUpdate();

    private void HooksUpdate()
    {
        var gfm = GameFlowManager.Instance;

        if (!_sessionWired && gfm != null && gfm.SessionState != null)
        {
            gfm.SessionState.GameStateChanged -= HandleGameStateChanged;
            gfm.SessionState.GameStateChanged += HandleGameStateChanged;
            _sessionWired = true;
        }

        if (!_motifHooksWired && gfm != null && gfm.phaseTransitionManager != null)
        {
            gfm.phaseTransitionManager.OnMotifChanged -= HandleMotifChanged;
            gfm.phaseTransitionManager.OnMotifChanged += HandleMotifChanged;
            gfm.OnMotifCompleted -= HandleMotifCompleted;
            gfm.OnMotifCompleted += HandleMotifCompleted;
            _motifHooksWired = true;
        }
    }

    private void HandleGameStateChanged(GameState state)
    {
        if (state == GameState.Playing)
        {
            StartNewRun();
            // phaseTransitionManager is a fresh instance each run — force HooksUpdate to
            // re-subscribe against it rather than keeping a stale subscription.
            _motifHooksWired = false;
        }
        else if (state == GameState.GameOver)
        {
            FinalizeRun("Completed");
        }
        else if (_runActive)
        {
            // Playing -> Selection/Begin without passing through GameOver: an early quit
            // (e.g. SceneFlowCoordinator.QuitToSelection).
            FinalizeRun("QuitEarly");
        }
    }

    private void StartNewRun()
    {
        _current = new MazeRunRecord
        {
            RunId = Guid.NewGuid().ToString(),
            StartedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        _runActive = true;
        _currentVisitOrder = -1;
    }

    private void FinalizeRun(string endReason)
    {
        if (_current == null || !_runActive) return;

        _current.EndedAtUtc = DateTime.UtcNow.ToString("o");
        _current.EndReason = endReason;
        string path = MazeTelemetryStore.SaveRun(_current);
        if (path != null) uploader?.UploadRun(_current, path);

        _runActive = false;
        _current = null;
    }

    // Fires whenever the run actually transitions into a new motif — including the run's very
    // first motif (oldMotif == null). This is the sole source of both path visits and edges:
    // whether the next motif was chosen by the player (PathChoiceCoordinator) or advanced
    // linearly (PhaseTransitionManager.AdvanceMotif/AdvancePhase), it always lands here.
    private void HandleMotifChanged(MotifProfile oldMotif, MotifProfile newMotif)
    {
        if (_current == null || newMotif == null) return;

        var ptm = GameFlowManager.Instance?.phaseTransitionManager;
        if (ptm == null) return;

        string phaseId = ptm.CurrentPhaseId;
        string motifId = newMotif.motifId;
        int order = _current.Path.Count;

        if (oldMotif != null && _current.Path.Count > 0)
        {
            var from = _current.Path[_current.Path.Count - 1];
            _current.Transitions.Add(new MotifTransition
            {
                FromPhaseId = from.PhaseId,
                FromMotifId = from.MotifId,
                ToPhaseId = phaseId,
                ToMotifId = motifId,
                TransitionOrder = _current.Transitions.Count,
            });
        }

        _current.Path.Add(new MotifVisit
        {
            PhaseId = phaseId,
            MotifId = motifId,
            VisitOrder = order,
        });
        _currentVisitOrder = order;
    }

    // Fires before the bridge coroutine tears anything down and before the next OnMotifChanged,
    // so phaseTransitionManager.currentMotif here still refers to the motif that just finished.
    private void HandleMotifCompleted()
    {
        if (_current == null) return;

        var gfm = GameFlowManager.Instance;
        var ptm = gfm?.phaseTransitionManager;
        var tracks = gfm?.controller?.tracks;
        var completedMotif = ptm?.currentMotif;
        if (ptm == null || tracks == null || completedMotif == null) return;

        string phaseId = ptm.CurrentPhaseId;
        string motifId = completedMotif.motifId;

        // HooksUpdate's lazy Update()-poll can lose the race for a run's very first motif:
        // PhaseTransitionManager.StartChapter fires OnMotifChanged synchronously right after
        // the new per-run instance is assigned, sometimes before our next Update() has
        // subscribed. When that happens Path is still empty here even though a motif is
        // completing — backfill the missing visit rather than dropping this solution.
        if (_current.Path.Count == 0)
        {
            _current.Path.Add(new MotifVisit { PhaseId = phaseId, MotifId = motifId, VisitOrder = 0 });
            _currentVisitOrder = 0;
        }

        var solution = new MotifSolution
        {
            PhaseId = phaseId,
            MotifId = motifId,
            VisitOrder = _currentVisitOrder,
        };

        foreach (var track in tracks)
        {
            if (track == null) continue;

            var voice = BuildVoice(track);
            if (voice != null) solution.Voices.Add(voice);
        }

        solution.Voices = solution.Voices
            .OrderBy(v => v.Role)
            .ThenBy(v => v.VoiceIndex)
            .ToList();

        _current.Solutions.Add(solution);
    }

    private static TelemetryTrackVoice BuildVoice(InstrumentTrack track)
    {
        var notes = track.GetPersistentLoopNotes();
        int binCount = Mathf.Max(1, track.loopMultiplier);

        var bins = new List<TelemetryTrackBin>(binCount);
        bool anyFilled = false;
        for (int b = 0; b < binCount; b++)
        {
            bool filled = track.IsBinFilled(b);
            if (filled) anyFilled = true;
            bins.Add(new TelemetryTrackBin { BinIndex = b, IsFilled = filled });
        }

        bool hasNotes = notes != null && notes.Count > 0;
        if (!hasNotes && !anyFilled) return null; // skip voices the player never touched

        var voice = new TelemetryTrackVoice
        {
            Role = track.assignedRole,
            VoiceIndex = track.voiceIndex,
            Bins = bins,
        };

        if (hasNotes)
        {
            voice.Notes = notes
                .Select(n => new TelemetryNoteEntry
                {
                    StepIndex = n.stepIndex,
                    MidiNote = n.note,
                    DurationTicks = n.duration,
                    Velocity = n.velocity,
                })
                .OrderBy(n => n.StepIndex)
                .ThenBy(n => n.MidiNote)
                .ToList();
        }

        return voice;
    }
}
