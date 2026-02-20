using System.Collections.Generic;
using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    [Header("Chapters (Phase -> Motifs)")]
    [SerializeField] private PhaseChapterLibrary chapterLibrary;
    [SerializeField] private bool loopChapters = true;   // "book loops back to chapter 1"
    [SerializeField] private bool holdOnLastChapter = false; // if not looping, stay on last vs return default
    
    public MazeArchetype previousPhase { get; private set; }
    public MazeArchetype currentPhase  { get; private set; }

    public MotifProfile currentMotif   { get; private set; }
    public int currentMotifIndex       { get; private set; } = -1;

    private List<MotifProfile> _chapterMotifs;
    private bool _chapterLoops = true;

    public event System.Action<MazeArchetype, MazeArchetype> OnPhaseChanged;
    public event System.Action<MotifProfile, MotifProfile>   OnMotifChanged;

    public NoteSetFactory noteSetFactory;

    // ---------------------------
    // CHAPTER START (PHASE)
    // ---------------------------

    // PhaseTransitionManager.cs
    private bool _chapterLoadedForCurrentPhase = false;
    public MazeArchetype ResolveNextPhase(MazeArchetype current)
    {
        if (chapterLibrary == null || chapterLibrary.chapters == null || chapterLibrary.chapters.Count == 0)
            return current; // safest fallback: no change

        // Find current index in authored chapter list
        int idx = chapterLibrary.chapters.FindIndex(c => c != null && c.phase == current);

        // If current isn't in the list, start at first authored chapter
        if (idx < 0)
            return chapterLibrary.chapters[0].phase;

        int next = idx + 1;

        if (next >= chapterLibrary.chapters.Count)
        {
            if (loopChapters)
                next = 0;
            else
                return holdOnLastChapter ? chapterLibrary.chapters[chapterLibrary.chapters.Count - 1].phase : current;
        }

        return chapterLibrary.chapters[next].phase;
    }
    public void EnsureChapterLoaded(MazeArchetype phase, string who)
    {
        // Already loaded for this phase? Do nothing. (CRITICAL: do NOT reset motif index.)
        if (_chapterLoadedForCurrentPhase &&
            currentPhase == phase &&
            _chapterMotifs != null &&
            _chapterMotifs.Count > 0)
        {
            return;
        }

        // We are loading (or re-loading) a chapter. Update phase bookkeeping.
        previousPhase = currentPhase;
        currentPhase  = phase;

        // Load motifs + loop flag from library
        LoadChapterForPhase(phase);
        _chapterLoadedForCurrentPhase = (_chapterMotifs != null && _chapterMotifs.Count > 0);

        // Initialize pointer ONLY because we just loaded a chapter (or swapped phases).
        currentMotifIndex = (_chapterLoadedForCurrentPhase) ? 0 : -1;
        currentMotif = (currentMotifIndex >= 0) ? _chapterMotifs[currentMotifIndex] : null;

        Debug.Log($"[CHAPTER] EnsureLoaded phase={phase} loaded={_chapterLoadedForCurrentPhase} loop={_chapterLoops} motif={(currentMotif ? currentMotif.motifId : "null")} idx={currentMotifIndex} by {who}");
    }

    public void StartChapter(MazeArchetype phase, string who)
    {
        var oldPhase = currentPhase;
        var oldMotif = currentMotif;

        previousPhase = currentPhase;
        currentPhase  = phase;

        // Force a reload + reset to paragraph 0
        _chapterLoadedForCurrentPhase = false;

        LoadChapterForPhase(phase);
        _chapterLoadedForCurrentPhase = (_chapterMotifs != null && _chapterMotifs.Count > 0);

        currentMotifIndex = (_chapterLoadedForCurrentPhase) ? 0 : -1;
        currentMotif = (currentMotifIndex >= 0) ? _chapterMotifs[currentMotifIndex] : null;

        if (oldPhase != currentPhase)
        {
            Debug.Log($"[CHAPTER] {oldPhase}→{currentPhase} by {who}");
            OnPhaseChanged?.Invoke(oldPhase, currentPhase);
        }
        else
        {
            Debug.Log($"[CHAPTER] Re-start {currentPhase} by {who}");
        }

        Debug.Log($"[MOTIF] Chapter init/apply: motif={(currentMotif ? currentMotif.motifId : "null")} idx={currentMotifIndex} by {who}");

        ApplyMotifToAudioAndTracks(
            oldMotif,
            currentMotif,
            armAtNextBoundary: false,
            restartTransport: false,
            who: $"PTM/StartChapter:{who}"
        );
    }

    public MotifProfile AdvanceMotif(string who, bool restartDrumsTransport = false)
    {
        if (_chapterMotifs == null || _chapterMotifs.Count == 0)
        {
            Debug.LogWarning($"[MOTIF] Advance ignored (no motifs for chapter phase={currentPhase}) by {who}");
            return null;
        }

        var oldMotif = currentMotif;

        int next = currentMotifIndex + 1;

        // Exhausted chapter?
        if (next >= _chapterMotifs.Count)
        {
            if (_chapterLoops)
            {
                next = 0; // loop back to start
            }
            else
            {
                // IMPORTANT: signal exhaustion to caller (GFM) so it can start next phase.
                Debug.Log($"[MOTIF] Exhausted chapter motifs (loop=false) phase={currentPhase} by {who}");
                return null;
            }
        }

        currentMotifIndex = next;
        currentMotif = _chapterMotifs[currentMotifIndex];

        Debug.Log($"[MOTIF] Advance by {who}: motif={(currentMotif ? currentMotif.motifId : "null")} idx={currentMotifIndex}/{_chapterMotifs.Count}");

        ApplyMotifToAudioAndTracks(
            oldMotif,
            currentMotif,
            armAtNextBoundary: true,
            restartTransport: restartDrumsTransport,
            who: $"PTM/AdvanceMotif:{who}"
        );

        return currentMotif;
    }

    // ---------------------------
    // INTERNALS
    // ---------------------------
    private void LoadChapterForPhase(MazeArchetype phase)
    {
        _chapterMotifs = null;
        _chapterLoops  = true;

        if (chapterLibrary == null)
        {
            Debug.LogError("[CHAPTER] No PhaseChapterLibrary assigned.");
            return;
        }

        var ch = chapterLibrary.Get(phase);
        if (ch == null || ch.motifs == null || ch.motifs.Count == 0)
        {
            Debug.LogError($"[CHAPTER] Missing/empty chapter for phase={phase}.");
            return;
        }

        _chapterMotifs = ch.motifs;
        _chapterLoops  = ch.loopMotifs;
    }

    private void ApplyMotifToAudioAndTracks(
        MotifProfile oldMotif,
        MotifProfile newMotif,
        bool armAtNextBoundary,
        bool restartTransport,
        string who)
    {
        if (newMotif == null) return;

        // Notify motif change
        if (oldMotif != newMotif)
            OnMotifChanged?.Invoke(oldMotif, newMotif);

        // Authoritative drums
        var drums = GameFlowManager.Instance?.activeDrumTrack;
        if (drums != null)
            drums.SetMotifBeatSequence(newMotif, armAtNextBoundary, who, restartTransport);

        // Notesets to tracks
        ConfigureTracksForCurrentPhaseAndMotif();
    }

    public void ConfigureTracksForCurrentPhaseAndMotif()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || noteSetFactory == null) return;

        var controller = gfm.controller;
        if (controller == null || controller.tracks == null) return;

        int baseEntropy = 0;

        foreach (var track in controller.tracks)
        {
            if (!track) continue;
            if (currentMotif == null) continue;

            var noteSet = noteSetFactory.Generate(track, currentMotif, baseEntropy);
            if (noteSet == null) continue;

            track.authoredRootMidi = currentMotif.keyRootMidi;
            track.SetNoteSet(noteSet);
        }
    }

    // IMPORTANT: Do NOT let DrumTrack/PhaseStar spawn mutate phase or motif here.
    private PhaseStarBehaviorProfile _activeBehaviorProfile;
    private void HandlePhaseStarSpawned(MazeArchetype phase, PhaseStarBehaviorProfile profile)
    {
        _activeBehaviorProfile = profile;
        // (No phase/motif mutations. That’s GFM’s job: chapter start / paragraph advance.)
    }

    void OnEnable()
    {
        var drums = GameFlowManager.Instance?.activeDrumTrack;
        if (drums != null) drums.OnPhaseStarSpawned += HandlePhaseStarSpawned;
    }

    void OnDisable()
    {
        var drums = GameFlowManager.Instance?.activeDrumTrack;
        if (drums != null) drums.OnPhaseStarSpawned -= HandlePhaseStarSpawned;
    }
}
