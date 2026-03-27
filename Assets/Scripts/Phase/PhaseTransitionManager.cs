using System.Collections.Generic;
using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    [Header("Chapters (Phase -> Motifs)")]
    [SerializeField] private PhaseLibrary chapterLibrary;
    [SerializeField] private bool loopChapters = true;
    [SerializeField] private bool holdOnLastChapter = false;

    // Phase index replaces MazeArchetype. Stub property kept for BridgeOrchestration compat.
    private int currentPhaseIndex  { get; set; } = -1;
    public int previousPhaseIndex { get; private set; } = -1;
    public MazeArchetype currentPhase => MazeArchetype.Windows; // compat stub — remove when MazeArchetype is fully gone

    public MotifProfile currentMotif   { get; private set; }
    public int currentMotifIndex       { get; private set; } = -1;

    private List<MotifProfile> _chapterMotifs;
    private bool _chapterLoops = true;

    public event System.Action OnPhaseChanged;
    public event System.Action<MotifProfile, MotifProfile> OnMotifChanged;

    public NoteSetFactory noteSetFactory;

    public int FirstPhaseIndex  => 0;
    public int PhaseCount       => (chapterLibrary != null && chapterLibrary.phases != null) ? chapterLibrary.phases.Count : 0;

    /// <summary>
    /// Advances to the next phase, respecting loopChapters and holdOnLastChapter settings.
    /// Calls StartChapter internally, which resets the motif index and applies the new motif.
    /// </summary>
    public void AdvancePhase(string who)
    {
        if (chapterLibrary == null || chapterLibrary.phases == null || chapterLibrary.phases.Count == 0)
        {
            Debug.LogWarning($"[CHAPTER] AdvancePhase: no phases in library, by {who}");
            return;
        }

        int count = chapterLibrary.phases.Count;
        int next  = currentPhaseIndex + 1;

        if (next >= count)
        {
            if (loopChapters)
            {
                next = 0;
            }
            else
            {
                Debug.Log($"[CHAPTER] All phases exhausted (loopChapters=false holdOnLast={holdOnLastChapter}); by {who}");
                if (holdOnLastChapter)
                    next = count - 1; // restart last phase's motifs from top
                else
                    return;           // no-op: stay on final motif
            }
        }

        StartChapter(next, who);
    }

    // ---------------------------
    // CHAPTER START (PHASE)
    // ---------------------------

    public void StartChapter(int phaseIndex, string who)
    {
        int oldIndex = currentPhaseIndex;
        var oldMotif = currentMotif;

        previousPhaseIndex = currentPhaseIndex;
        currentPhaseIndex  = phaseIndex;

        LoadChapterForPhase(phaseIndex);

        currentMotifIndex = (_chapterMotifs != null && _chapterMotifs.Count > 0) ? 0 : -1;
        currentMotif      = (currentMotifIndex >= 0) ? _chapterMotifs[currentMotifIndex] : null;

        if (oldIndex != currentPhaseIndex)
        {
            Debug.Log($"[CHAPTER] phase {oldIndex}→{currentPhaseIndex} by {who}");
            OnPhaseChanged?.Invoke();
        }
        else
        {
            Debug.Log($"[CHAPTER] Re-start phase {currentPhaseIndex} by {who}");
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
            Debug.LogWarning($"[MOTIF] Advance ignored (no motifs for phase={currentPhaseIndex}) by {who}");
            return null;
        }

        var oldMotif = currentMotif;
        int next = currentMotifIndex + 1;

        if (next >= _chapterMotifs.Count)
        {
            if (_chapterLoops)
            {
                next = 0;
            }
            else
            {
                Debug.Log($"[MOTIF] Exhausted chapter motifs (loop=false) phase={currentPhaseIndex} by {who}");
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

    private void LoadChapterForPhase(int phaseIndex)
    {
        _chapterMotifs = null;
        _chapterLoops  = loopChapters;

        if (chapterLibrary == null)
        {
            Debug.LogError("[CHAPTER] No PhaseLibrary assigned to PhaseTransitionManager.");
            return;
        }

        if (chapterLibrary.phases == null || chapterLibrary.phases.Count == 0)
        {
            Debug.LogError("[CHAPTER] PhaseLibrary has no phases.");
            return;
        }

        int idx = Mathf.Clamp(phaseIndex, 0, chapterLibrary.phases.Count - 1);
        var phase = chapterLibrary.phases[idx];

        if (phase == null || phase.motifs == null || phase.motifs.Count == 0)
        {
            Debug.LogError($"[CHAPTER] Phase {idx} ('{phase?.phaseName}') has no motifs.");
            return;
        }

        _chapterMotifs = phase.motifs;
        _chapterLoops  = phase.loopMotifs;

        Debug.Log($"[CHAPTER] Loaded phase {idx} '{phase.phaseName}' with {_chapterMotifs.Count} motif(s), loop={_chapterLoops}");
    }

    private void ApplyMotifToAudioAndTracks(
        MotifProfile oldMotif,
        MotifProfile newMotif,
        bool armAtNextBoundary,
        bool restartTransport,
        string who)
    {
        if (newMotif == null) return;

        if (oldMotif != newMotif)
            OnMotifChanged?.Invoke(oldMotif, newMotif);

        var drums = GameFlowManager.Instance?.activeDrumTrack;
        if (drums != null)
            drums.SetMotifBeatSequence(newMotif, armAtNextBoundary, who, restartTransport);

        ConfigureTracksForCurrentPhaseAndMotif();
    }

    private void ConfigureTracksForCurrentPhaseAndMotif()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || noteSetFactory == null) return;

        var controller = gfm.controller;
        if (controller == null || controller.tracks == null) return;

        foreach (var track in controller.tracks)
        {
            if (!track) continue;
            if (currentMotif == null) continue;

            NoteSet bin0NoteSet = null;
            for (int b = 0; b < track.maxLoopMultiplier; b++)
            {
                var noteSet = noteSetFactory.GenerateForBin(track, currentMotif, binIndex: b, entropy: 0);
                if (noteSet == null) continue;

                track.SetNoteSetForBin(b, noteSet);
                if (b == 0) bin0NoteSet = noteSet;
            }

            if (bin0NoteSet == null) continue;

            track.authoredRootMidi = currentMotif.keyRootMidi;
            track.SetNoteSet(bin0NoteSet);

            var cfg = currentMotif.GetConfigForRoleAtBin(track.assignedRole, 0, track.maxLoopMultiplier);
            track.RefreshRoleColorsFromProfile(cfg?.roleProfile);
        }
    }

    private void HandlePhaseStarSpawned(PhaseStarBehaviorProfile profile) { }

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
