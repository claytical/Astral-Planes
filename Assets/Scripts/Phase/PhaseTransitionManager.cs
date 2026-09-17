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
    public int CurrentPhaseIndex => currentPhaseIndex;

    public string CurrentPhaseId =>
        (chapterLibrary != null && chapterLibrary.phases != null
         && currentPhaseIndex >= 0 && currentPhaseIndex < chapterLibrary.phases.Count)
            ? chapterLibrary.phases[currentPhaseIndex].phaseId
            : null;
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

    /// <summary>Indices after currentMotifIndex within the current phase's motif list, in order, not wrapped.</summary>
    public IReadOnlyList<int> GetRemainingMotifIndices()
    {
        var result = new List<int>();
        if (_chapterMotifs == null) return result;
        for (int i = currentMotifIndex + 1; i < _chapterMotifs.Count; i++)
            result.Add(i);
        return result;
    }

    public int RemainingMotifCount => Mathf.Max(0, (_chapterMotifs?.Count ?? 0) - (currentMotifIndex + 1));

    /// <summary>Motif at motifIndex within the current phase's motif list, without changing state. Null if out of range.</summary>
    public MotifProfile PeekMotifInCurrentPhase(int motifIndex)
    {
        if (_chapterMotifs == null || motifIndex < 0 || motifIndex >= _chapterMotifs.Count) return null;
        return _chapterMotifs[motifIndex];
    }

    /// <summary>
    /// Single next phase index, respecting loopChapters/holdOnLastChapter — the one source of
    /// truth for "what phase comes after this one," shared by AdvancePhase and
    /// GetRemainingPhaseIndices so they can't disagree. -1 = nothing next (last phase,
    /// loopChapters=false, holdOnLastChapter=false).
    /// </summary>
    private int ComputeNextPhaseIndex()
    {
        if (chapterLibrary == null || chapterLibrary.phases == null || chapterLibrary.phases.Count == 0)
            return -1;

        int count = chapterLibrary.phases.Count;
        int next  = currentPhaseIndex + 1;

        if (next >= count)
        {
            if (loopChapters) next = 0;
            else if (holdOnLastChapter) next = count - 1;
            else return -1;
        }

        return next;
    }

    /// <summary>
    /// The single next phase index, as a list of at most one element — a maze exit should
    /// only ever lead to the immediate next phase's motif 0, never skip ahead or offer a
    /// menu of other phases. Empty when there is no next phase (see ComputeNextPhaseIndex).
    /// </summary>
    public IReadOnlyList<int> GetRemainingPhaseIndices()
    {
        var result = new List<int>();
        int next = ComputeNextPhaseIndex();
        if (next >= 0) result.Add(next);
        return result;
    }

    public int RemainingPhaseCount => GetRemainingPhaseIndices().Count;

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

        int next = ComputeNextPhaseIndex();
        if (next < 0)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[CHAPTER] All phases exhausted (loopChapters=false holdOnLast={holdOnLastChapter}); by {who}");
            return; // no-op: stay on final motif
        }

        StartChapter(next, who);
    }

    // ---------------------------
    // CHAPTER START (PHASE)
    // ---------------------------

    /// <summary>
    /// Like StartChapter, but resolves the target by stable phaseId first (if the
    /// PhaseLibrary asset's phase order has been reordered since phaseId/fallbackIndex
    /// were recorded, e.g. an edit that inserted/removed/reordered phases). Falls back
    /// to the raw fallbackIndex when phaseId is null/empty or isn't found.
    /// </summary>
    public void StartChapterById(string phaseId, int fallbackIndex, string who)
    {
        int resolvedIndex = fallbackIndex;

        if (!string.IsNullOrEmpty(phaseId) && chapterLibrary?.phases != null)
        {
            int idx = chapterLibrary.phases.FindIndex(p => p != null && p.phaseId == phaseId);
            if (idx >= 0)
                resolvedIndex = idx;
            else
                Debug.LogWarning($"[CHAPTER] StartChapterById: phaseId '{phaseId}' not found; falling back to stored index {fallbackIndex} by {who}");
        }

        StartChapter(resolvedIndex, who);
    }

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
            if (GameFlowManager.VerboseLogging) Debug.Log($"[CHAPTER] phase {oldIndex}→{currentPhaseIndex} by {who}");
            OnPhaseChanged?.Invoke();
        }
        else
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[CHAPTER] Re-start phase {currentPhaseIndex} by {who}");
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[MOTIF] Chapter init/apply: motif={(currentMotif ? currentMotif.motifId : "null")} idx={currentMotifIndex} by {who}");

        ApplyMotifToAudioAndTracks(
            oldMotif,
            currentMotif,
            armAtNextBoundary: true,
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
                if (GameFlowManager.VerboseLogging) Debug.Log($"[MOTIF] Exhausted chapter motifs (loop=false) phase={currentPhaseIndex} by {who}");
                return null;
            }
        }

        currentMotifIndex = next;
        currentMotif = _chapterMotifs[currentMotifIndex];

        if (GameFlowManager.VerboseLogging) Debug.Log($"[MOTIF] Advance by {who}: motif={(currentMotif ? currentMotif.motifId : "null")} idx={currentMotifIndex}/{_chapterMotifs.Count}");

        ApplyMotifToAudioAndTracks(
            oldMotif,
            currentMotif,
            armAtNextBoundary: true,
            restartTransport: restartDrumsTransport,
            who: $"PTM/AdvanceMotif:{who}"
        );

        return currentMotif;
    }

    /// <summary>
    /// Like JumpToMotifIndex, but treats (phaseId, fallbackIndex) as authoritative — that
    /// pair is the ring's actual on-disk identity (RingSessionStore keys ring files by
    /// phaseIndex_motifIndex) — and uses motifId only as a consistency check, never as a
    /// silent override. If fallbackIndex resolves to a motif whose motifId doesn't match
    /// the stored motifId, that's data drift/corruption: log it loudly and still trust the
    /// index. motifId is only used to resolve the target when fallbackIndex is genuinely
    /// out of range for the current phase (e.g. the phase's motif list has actually
    /// shrunk since this ring was saved) — the one case where the stored index truly
    /// can't be trusted.
    /// </summary>
    public void JumpToMotif(string motifId, int fallbackIndex, string who)
    {
        int resolvedIndex = fallbackIndex;

        if (_chapterMotifs != null && fallbackIndex >= 0 && fallbackIndex < _chapterMotifs.Count)
        {
            var atIndex = _chapterMotifs[fallbackIndex];
            if (!string.IsNullOrEmpty(motifId) && atIndex != null && atIndex.motifId != motifId)
            {
                Debug.LogWarning($"[MOTIF] JumpToMotif: stored index {fallbackIndex} resolves to motifId " +
                                  $"'{atIndex.motifId}', but ring's stored MotifId is '{motifId}' — using the " +
                                  $"stored index (ring data may be stale/corrupted) by {who}");
            }
        }
        else if (!string.IsNullOrEmpty(motifId) && _chapterMotifs != null)
        {
            int matchCount = 0;
            for (int i = 0; i < _chapterMotifs.Count; i++)
            {
                if (_chapterMotifs[i] != null && _chapterMotifs[i].motifId == motifId)
                    matchCount++;
            }

            if (matchCount > 1)
            {
                Debug.LogWarning($"[MOTIF] JumpToMotif: stored index {fallbackIndex} out of range and motifId " +
                                  $"'{motifId}' is ambiguous ({matchCount} matches) in current phase by {who}");
            }
            else
            {
                int idx = _chapterMotifs.FindIndex(m => m != null && m.motifId == motifId);
                if (idx >= 0)
                {
                    Debug.LogWarning($"[MOTIF] JumpToMotif: stored index {fallbackIndex} out of range " +
                                      $"(count={_chapterMotifs.Count}); recovered motif '{motifId}' at index {idx} by {who}");
                    resolvedIndex = idx;
                }
                else
                {
                    Debug.LogWarning($"[MOTIF] JumpToMotif: stored index {fallbackIndex} out of range and motifId " +
                                      $"'{motifId}' not found in current phase (count={_chapterMotifs.Count}) by {who}");
                }
            }
        }

        JumpToMotifIndex(resolvedIndex, who);
    }

    /// <summary>
    /// Override the active motif to a specific index within the current phase without
    /// resetting phase state. Applies the new motif to audio and tracks immediately.
    /// Intended for use during scene setup when starting from a PhaseLibrary selection.
    /// </summary>
    public void JumpToMotifIndex(int motifIdx, string who)
    {
        if (_chapterMotifs == null || motifIdx < 0 || motifIdx >= _chapterMotifs.Count)
        {
            Debug.LogWarning($"[MOTIF] JumpToMotifIndex {motifIdx} out of range (count={_chapterMotifs?.Count ?? 0}) by {who}");
            return;
        }

        var prev = currentMotif;
        currentMotifIndex = motifIdx;
        currentMotif      = _chapterMotifs[motifIdx];

        if (GameFlowManager.VerboseLogging) Debug.Log($"[MOTIF] JumpToMotifIndex {motifIdx}: motif={(currentMotif ? currentMotif.motifId : "null")} by {who}");

        ApplyMotifToAudioAndTracks(
            prev,
            currentMotif,
            armAtNextBoundary: true,
            restartTransport: false,
            who: $"PTM/JumpToMotif:{who}"
        );
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

        if (phaseIndex < 0 || phaseIndex >= chapterLibrary.phases.Count)
        {
            Debug.LogError($"[CHAPTER] LoadChapterForPhase: index {phaseIndex} out of range (count={chapterLibrary.phases.Count}); no chapter loaded.");
            return;
        }
        var phase = chapterLibrary.phases[phaseIndex];

        if (phase == null || phase.motifs == null || phase.motifs.Count == 0)
        {
            Debug.LogError($"[CHAPTER] Phase {phaseIndex} ('{phase?.phaseName}') has no motifs.");
            return;
        }

        _chapterMotifs = phase.motifs;
        _chapterLoops  = phase.loopMotifs;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[CHAPTER] Loaded phase {phaseIndex} '{phase.phaseName}' with {_chapterMotifs.Count} motif(s), loop={_chapterLoops}");
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
//        GameFlowManager.Instance.harmony.SetActiveProfile(newMotif.chordProgression, applyImmediately: true);

        ConfigureTracksForCurrentPhaseAndMotif();
    }

    private void ConfigureTracksForCurrentPhaseAndMotif()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || noteSetFactory == null) return;

        var controller = gfm.controller;
        if (controller == null) return;

        // Rebuild tracks for this motif's active voices before configuring NoteSets on them —
        // track construction is motif-driven (RoleVoiceKey per active voice), not scene-authored.
        controller.RebuildTracksForMotif(currentMotif);
        if (controller.tracks == null) return;

        foreach (var track in controller.tracks)
        {
            if (!track) continue;
            if (currentMotif == null) continue;

            // Set the motif's intended profile BEFORE generating NoteSets so that
            // NoteSet generation and commit-time ShiftByOctavesIntoTrackRange both
            // use the same highestAllowedNote (avoids octave-note collapse at commit).
            track.RefreshRoleColorsFromProfile(currentMotif.GetInstrumentProfileForVoice(track.assignedRole, track.voiceIndex));
            var cfg = currentMotif.GetConfigForRoleAtBin(track.assignedRole, 0, track.maxLoopMultiplier, track.voiceIndex);

            NoteSet bin0NoteSet = null;
            for (int b = 0; b < track.maxLoopMultiplier; b++)
            {
                var noteSet = noteSetFactory.GenerateForBin(track, currentMotif, binIndex: b, entropy: 0);
                if (noteSet == null) continue;

                track.SetNoteSetForBin(b, noteSet);
                if (b == 0) bin0NoteSet = noteSet;
            }

            if (bin0NoteSet == null) continue;

            // If the riff spans more than one base loop, start the track at the matching
            // multiplier so GetLeaderSteps() reaches every authored step.
            var drum = gfm.activeDrumTrack;
            int drumBaseSteps = (drum != null && drum.totalSteps > 0) ? drum.totalSteps : 0;
            if (drumBaseSteps > 0 && cfg?.riff != null)
            {
                int riffLoopSteps = cfg.riff.riff.loopSteps;
                if (riffLoopSteps > drumBaseSteps && riffLoopSteps % drumBaseSteps == 0)
                {
                    int requiredMul = Mathf.Clamp(riffLoopSteps / drumBaseSteps, 1, track.maxLoopMultiplier);
                    if (track.loopMultiplier < requiredMul)
                        track.loopMultiplier = requiredMul;
                }
            }

            track.authoredRootMidi = currentMotif.keyRootMidi;
            track.SetNoteSet(bin0NoteSet);
        }

        controller.ResyncLeaderBinsNow();
    }

}
