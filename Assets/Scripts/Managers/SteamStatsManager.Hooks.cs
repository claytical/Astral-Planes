// Gameplay-event wiring for the analytics funnel. Every subscription here is external —
// none of GameFlowManager, ControlTutorialDirector, PhaseTransitionManager, or
// InstrumentTrackController know analytics exists. See the analytics plan doc for the
// stat -> trigger mapping.
public partial class SteamStatsManager
{
    private int _motifOrdinal;
    private bool _firstCommitDone;

    private bool _sessionStateWired;
    private bool _tutorialWired;
    private bool _motifHooksWired;

    partial void HooksUpdate()
    {
        var gfm = GameFlowManager.Instance;

        if (!_sessionStateWired && gfm != null && gfm.SessionState != null)
        {
            gfm.SessionState.GameStateChanged -= HandleGameStateChanged;
            gfm.SessionState.GameStateChanged += HandleGameStateChanged;
            _sessionStateWired = true;
        }

        // Tutorial completion fires before GameState.Playing, so this must be caught by its
        // own poll rather than piggybacking on the Playing-state wiring below.
        if (!_tutorialWired && ControlTutorialDirector.Instance != null)
        {
            ControlTutorialDirector.Instance.OnTutorialCompleted -= HandleTutorialCompleted;
            ControlTutorialDirector.Instance.OnTutorialCompleted += HandleTutorialCompleted;
            _tutorialWired = true;
        }
    }

    private void HandleGameStateChanged(GameState state)
    {
        if (state == GameState.Playing)
        {
            SetMilestoneOnce(SteamStatNames.GameplayStarted);
            _motifOrdinal = 0;
            WireTracksHook();
            WireMotifHooks();
            FlushIfDirty();
        }
        else if (state == GameState.GameOver)
        {
            SetMilestoneOnce(SteamStatNames.NaturalRunEndReached);
            FlushIfDirty();
        }
    }

    private void HandleTutorialCompleted()
    {
        SetMilestoneOnce(SteamStatNames.TutorialCompleted);
        FlushIfDirty();
    }

    // InstrumentTrackController.RebuildTracksForMotif destroys and re-instantiates every
    // InstrumentTrack on every motif change, so per-track event subscriptions must be
    // re-taken each rebuild rather than once at session start (mirrors StarPool's own
    // SubscribeToTracks/UnsubscribeFromTracks handling of the same churn).
    private void WireTracksHook()
    {
        if (_firstCommitDone) return;
        var controller = GameFlowManager.Instance?.controller;
        if (controller == null) return;

        controller.OnTracksRebuilt -= HandleTracksRebuilt;
        controller.OnTracksRebuilt += HandleTracksRebuilt;

        // Cover tracks that already exist right now (e.g. the first motif of the run, whose
        // rebuild already happened before this hook was wired).
        HandleTracksRebuilt(controller.tracks);
    }

    private void HandleTracksRebuilt(InstrumentTrack[] tracks)
    {
        if (_firstCommitDone || tracks == null) return;

        foreach (var track in tracks)
        {
            if (track == null) continue;
            track.OnCollectableBurstCleared -= HandleBurstCleared;
            track.OnCollectableBurstCleared += HandleBurstCleared;
        }
    }

    private void HandleBurstCleared(InstrumentTrack track, int burstId, bool hadNotes)
    {
        if (!hadNotes || _firstCommitDone) return;
        _firstCommitDone = true;
        SetMilestoneOnce(SteamStatNames.FirstCommitCompleted);
        FlushIfDirty();
    }

    private void WireMotifHooks()
    {
        if (_motifHooksWired) return;
        var gfm = GameFlowManager.Instance;
        var ptm = gfm?.phaseTransitionManager;
        if (gfm == null || ptm == null) return;

        ptm.OnMotifChanged -= HandleMotifChanged;
        ptm.OnMotifChanged += HandleMotifChanged;
        gfm.OnMotifCompleted -= HandleMotifCompleted;
        gfm.OnMotifCompleted += HandleMotifCompleted;
        _motifHooksWired = true;
    }

    // Fires only on genuine committed transitions (StartChapter/AdvanceMotif/JumpToMotif),
    // never on menu preview, and the tutorial never touches PhaseTransitionManager — so
    // every firing here is inherently a real, non-tutorial motif.
    private void HandleMotifChanged(MotifProfile oldMotif, MotifProfile newMotif)
    {
        if (newMotif == null) return;
        _motifOrdinal++;
        var stat = SteamStatNames.MotifStarted(_motifOrdinal);
        if (stat == null) return;
        SetMilestoneOnce(stat);
        FlushIfDirty();
    }

    // Fires from GameFlowManager.BeginMotifBridge at the moment StarPool's bridge gate
    // passes — i.e. the motif actually reached its completed state — which is earlier and
    // more reliable than inferring completion from the next OnMotifChanged (there's a
    // bridge/cinematic gap between them; a quit during that gap would otherwise be missed).
    private void HandleMotifCompleted()
    {
        var stat = SteamStatNames.MotifCompleted(_motifOrdinal);
        if (stat != null) SetMilestoneOnce(stat);

        // Every real (non-tutorial) motif completion counts toward the lifetime total,
        // regardless of ordinal — tutorial completion never calls this handler.
        IncrementCounter(SteamStatNames.MotifsCompletedLifetime);
        FlushIfDirty();
    }
}
