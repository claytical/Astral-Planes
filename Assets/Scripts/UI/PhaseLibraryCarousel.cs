using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Bounded 2D carousel for selecting a saved motif ring in the Main scene.
///
/// Rows are phases (PhaseLibrary.phases); each row holds that phase's motifs in order.
/// East/west moves within the current row's motifs. North/south switches to the phase
/// row above/below, always landing on that row's first motif (index 0) — matching how a
/// phase is actually entered during play.
///
/// Slots are fixed world-space positions sorted left-to-right; only the active row is
/// displayed at a time, reusing the same slot objects. Centre index = slots.Length / 2.
///
/// Only "reached" phases get a row: phase 0, plus each subsequent phase whose predecessor
/// has every motif recorded. A reached phase's row holds one entry per authored motif in
/// that phase — a played motif (has a saved MotifSnapshot — audible, full ring detail), or
/// an unplayed one rendered as a locked preview (gray/unlit rings, count = nodesPerStar) via
/// ApplyLockedPreview. Locked entries are selectable — confirming one jumps straight into
/// that motif — but never previewed with audio. This is what keeps the player from moving
/// down to the next phase until the current one is fully recorded: there's simply no row
/// for it yet.
///
/// Bounded (no wrapping):
///   - Horizontally, slots whose data index falls outside [0, row.Count-1] are deactivated;
///     navigation halts at the row's edges.
///   - Vertically, north/south are no-ops once _currentRow is at 0 or _rows.Count-1 — so a
///     player who has only ever played one phase can't move up or down at all.
///
/// No entries at all (nothing played yet):
///   - All slots hidden; pressToStartCue shown. The locked-preview entry is not offered here.
///   - Any confirm input loads trackSelectionScene directly.
///
/// Input: direct polling of Gamepad.current / Keyboard.current.
/// No PlayerInput required — compatible with Main scene's any-device model.
/// </summary>
public class PhaseLibraryCarousel : MonoBehaviour
{
    [SerializeField] private MotifRingGlyphApplicator[] slots;
    [SerializeField] private float slideDuration = 0.25f;
    [SerializeField] private string trackSelectionScene = "TrackSelection";
    [SerializeField] private float inputCooldown = 0.2f;

    [Tooltip("Alpha applied to non-center (unselected) carousel slots.")]
    [Range(0f, 1f)]
    [SerializeField] private float unselectedAlpha = 0.25f;

    [Tooltip("Shown when no ring records exist yet. Hide when rings are present.")]
    [SerializeField] private GameObject pressToStartCue;

    [Tooltip("Plays audio preview of the centered ring.")]
    [SerializeField] private RingPreviewPlayer previewPlayer;

    [Tooltip("Authoritative motif list, used to find the next not-yet-completed motif to offer as a locked preview.")]
    [SerializeField] private PhaseLibrary phaseLibrary;

#if UNITY_EDITOR
    [Header("Debug (Editor Only)")]
    [Tooltip("Design-time only: shows and allows navigating to every phase/motif in " +
             "PhaseLibrary regardless of completion state. Never available in player builds.")]
    [SerializeField] private bool showAllPhasesForDesign = false;
#endif

    /// <summary>Fired when the player confirms a motif, before the scene loads.</summary>
    public UnityEvent onMotifConfirmed;

    /// <summary>One carousel slot's worth of data: either a recorded snapshot or a not-yet-played motif offered as a locked preview.</summary>
    private struct CarouselEntry
    {
        public MotifSnapshot Snapshot;   // non-null for a played/recorded motif
        public MotifProfile  Profile;    // non-null only for the locked-preview entry
        public string PhaseId;
        public int    PhaseIndex;
        public int    MotifIndex;
        public string MotifId;
        public bool   IsLocked => Snapshot == null;
    }

    /// <summary>Rows are reached phases; each row is that phase's full authored motif list
    /// (played snapshots plus a locked entry for every not-yet-recorded motif), in motif order.</summary>
    private List<List<CarouselEntry>> _rows = new();
    private int   _currentRow;
    private int   _currentCol;
    private float _slotSpacing;
    private bool  _sliding;
    private bool  _confirming;
    private float _nextInputTime;
    private Vector3[] _homePositions;

    private List<CarouselEntry> CurrentRow => _rows[_currentRow];

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start()
    {
        var rings = RingSessionStore.LoadAllRingsFromDisk();
        List<CarouselEntry> entries;

#if UNITY_EDITOR
        if (showAllPhasesForDesign)
        {
            entries = BuildAllLibraryEntries(rings);
        }
        else
#endif
        {
            entries = rings.Count > 0 ? BuildReachedPhaseEntries(rings) : new List<CarouselEntry>();
        }

        entries.Sort((a, b) => a.PhaseIndex != b.PhaseIndex
            ? a.PhaseIndex.CompareTo(b.PhaseIndex)
            : a.MotifIndex.CompareTo(b.MotifIndex));

        // Bucket the flat, sorted entries into per-phase rows. Entries are already in
        // (PhaseIndex, MotifIndex) order, so each bucket comes out motif-ordered for free.
        _rows = entries
            .GroupBy(e => e.PhaseIndex)
            .Select(g => g.ToList())
            .ToList();

        InitSlotLayout();

        bool hasEntries = entries.Count > 0;
        if (pressToStartCue != null) pressToStartCue.SetActive(!hasEntries);

        if (hasEntries)
        {
            _currentRow = 0;
            _currentCol = 0;
            Refresh();
            UpdatePreviewForCenter();
        }
        else
        {
            foreach (var s in slots) s.gameObject.SetActive(false);
        }

        if (previewPlayer != null)
        {
            previewPlayer.OnBinIndexChanged += HandlePreviewBinChanged;
            previewPlayer.OnNoteFired       += HandleNoteFired;
        }
    }

    private void OnDestroy()
    {
        if (previewPlayer != null)
        {
            previewPlayer.OnBinIndexChanged -= HandlePreviewBinChanged;
            previewPlayer.OnNoteFired       -= HandleNoteFired;
        }
    }

    // Highlights the centered slot's ring(s) matching the bin RingPreviewPlayer is currently
    // sounding; -1 (playback stopped) clears any highlight instead. Only the centered slot ever
    // plays audio, so this is the only slot that ever receives highlight calls.
    private void HandlePreviewBinChanged(int binIndex)
    {
        int center = slots.Length / 2;
        if (center < 0 || center >= slots.Length || slots[center] == null) return;

        if (binIndex < 0) slots[center].ClearPlayingBinHighlight();
        else               slots[center].SetPlayingBin(binIndex);
    }

    // Spawns the note-position firework on the centered slot only, matching HandlePreviewBinChanged.
    private void HandleNoteFired(MusicalRole role, int binIndex, float angleRad)
    {
        int center = slots.Length / 2;
        if (center < 0 || center >= slots.Length || slots[center] == null) return;
        slots[center].SpawnNoteFirework(role, binIndex, angleRad);
    }

    // Walks the authored PhaseLibrary from phase 0, emitting one entry per motif (recorded or
    // locked) for each phase the player has reached. A phase counts as reached once its
    // predecessor has every motif recorded; the walk stops after the first phase that isn't
    // itself fully recorded, so no row is built for phases beyond that frontier. Snapshots are
    // matched by stable PhaseId/MotifId rather than raw index — safe against PhaseLibrary
    // reordering, same convention as PhaseTransitionManager.JumpToMotif.
    private List<CarouselEntry> BuildReachedPhaseEntries(List<MotifSnapshot> rings)
    {
        var result = new List<CarouselEntry>();
        if (phaseLibrary == null || phaseLibrary.phases == null) return result;

        for (int p = 0; p < phaseLibrary.phases.Count; p++)
        {
            var phase = phaseLibrary.phases[p];
            if (phase?.motifs == null) continue;

            bool phaseFullyRecorded = true;
            for (int m = 0; m < phase.motifs.Count; m++)
            {
                var motif = phase.motifs[m];
                if (motif == null) continue;

                var snap = rings.Find(r => r.PhaseId == phase.phaseId && r.MotifId == motif.motifId);
                if (snap != null)
                {
                    result.Add(new CarouselEntry
                    {
                        Snapshot   = snap,
                        PhaseId    = phase.phaseId,
                        PhaseIndex = p,
                        MotifIndex = m,
                        MotifId    = motif.motifId,
                    });
                }
                else
                {
                    phaseFullyRecorded = false;
                    result.Add(new CarouselEntry
                    {
                        Snapshot   = null,
                        Profile    = motif,
                        PhaseId    = phase.phaseId,
                        PhaseIndex = p,
                        MotifIndex = m,
                        MotifId    = motif.motifId,
                    });
                }
            }

            if (!phaseFullyRecorded) break;
        }
        return result;
    }

#if UNITY_EDITOR
    // Design-time only: one entry per phase×motif in the full authored PhaseLibrary,
    // regardless of completion. Played motifs use their recorded snapshot (full audio/
    // visual preview); everything else renders as a locked preview, same treatment as
    // the single "next up" entry already used in normal play.
    private List<CarouselEntry> BuildAllLibraryEntries(List<MotifSnapshot> rings)
    {
        var result = new List<CarouselEntry>();
        if (phaseLibrary == null || phaseLibrary.phases == null) return result;

        for (int p = 0; p < phaseLibrary.phases.Count; p++)
        {
            var phase = phaseLibrary.phases[p];
            if (phase?.motifs == null) continue;

            for (int m = 0; m < phase.motifs.Count; m++)
            {
                var motif = phase.motifs[m];
                if (motif == null) continue;

                var snap = rings.Find(r => r.PhaseIndex == p && r.MotifIndex == m);
                result.Add(snap != null
                    ? new CarouselEntry
                      {
                          Snapshot   = snap,
                          PhaseId    = phase.phaseId,
                          PhaseIndex = p,
                          MotifIndex = m,
                          MotifId    = snap.MotifId,
                      }
                    : new CarouselEntry
                      {
                          Snapshot   = null,
                          Profile    = motif,
                          PhaseId    = phase.phaseId,
                          PhaseIndex = p,
                          MotifIndex = m,
                          MotifId    = motif.motifId,
                      });
            }
        }
        return result;
    }
#endif

    // ── Setup ──────────────────────────────────────────────────────────────────

    private void InitSlotLayout()
    {
        // Sort provided slots left-to-right by world X position.
        var ordered = slots.OrderBy(s => s.transform.position.x).ToArray();
        for (int i = 0; i < slots.Length; i++) slots[i] = ordered[i];

        _homePositions = slots.Select(s => s.transform.position).ToArray();
        _slotSpacing   = slots.Length > 1
            ? Mathf.Abs(_homePositions[1].x - _homePositions[0].x)
            : 5f;
    }

    // ── Display ────────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var row = CurrentRow;
        int center = slots.Length / 2;
        for (int i = 0; i < slots.Length; i++)
        {
            int  dataIdx  = _currentCol + (i - center);
            bool inBounds = dataIdx >= 0 && dataIdx < row.Count;

            slots[i].gameObject.SetActive(inBounds);
            if (!inBounds) continue;

            // Each slot's own MotifRingGlyphApplicator fields (maxDisplayRadius, minDisplayRadius,
            // min/maxRingCountForSizing) are configured per-slot in the prefab — the Selected slot
            // is sized for the screen-relative "hero record" role, Previous/Next for the smaller
            // thumbnail role. Deliberately not overwritten here uniformly (as it used to be via a
            // shared maxRecordRadius field) — under the count-driven sizing model that would scale
            // the thumbnails up to match the Selected slot's target instead of staying small.
            var entry = row[dataIdx];
            bool isCenter = i == center;

            if (entry.IsLocked)
                slots[i].ApplyLockedPreview(entry.Profile.nodesPerStar, isCenter ? 1f : unselectedAlpha);
            else if (isCenter)
                slots[i].AnimateApply(entry.Snapshot);
            else
                slots[i].ApplyVinyl(entry.Snapshot, unselectedAlpha);
        }
    }

    private void UpdatePreviewForCenter()
    {
        if (previewPlayer == null) return;
        var entry = CurrentRow[_currentCol];
        if (entry.IsLocked) previewPlayer.Stop();
        else                previewPlayer.Play(entry.Snapshot);
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    private void Update()
    {
        // This polls raw device state directly (no Input Action / EventSystem involved), so it
        // has no built-in awareness of modal UI on top of it. The settings panel's own Confirm
        // press must not also be read here as "confirm the carousel and load TrackSelection" —
        // same guard role as the one in IntroInputHandler.OnAnyInput / LocalPlayer.HandleConfirm.
        // ShouldSuppressInput (not just IsOpen) also covers the same frame the panel just closed
        // on this exact press — Close() runs synchronously inside the Confirm callback, earlier
        // in this same frame, which would otherwise clear IsOpen before this Update() runs.
        if (SettingsPanelController.ShouldSuppressInput) return;

        bool next = false, prev = false, up = false, down = false, confirm = false;

        var gp = Gamepad.current;
        if (gp != null)
        {
            next    |= gp.dpad.right.wasPressedThisFrame;
            prev    |= gp.dpad.left.wasPressedThisFrame;
            up      |= gp.dpad.up.wasPressedThisFrame;
            down    |= gp.dpad.down.wasPressedThisFrame;
            confirm |= gp.buttonSouth.wasPressedThisFrame;
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            next    |= kb.rightArrowKey.wasPressedThisFrame;
            prev    |= kb.leftArrowKey.wasPressedThisFrame;
            up      |= kb.upArrowKey.wasPressedThisFrame;
            down    |= kb.downArrowKey.wasPressedThisFrame;
            confirm |= kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame;
        }

        if (_rows.Count == 0)
        {
            if (confirm) SceneManager.LoadScene(trackSelectionScene);
            return;
        }

        if (_sliding || _confirming) return; // ignore all input mid-slide/confirm so Confirm() never reads stale row/col

        if (confirm) { Confirm(); return; }

        if (Time.unscaledTime >= _nextInputTime)
        {
            if      (next) { _nextInputTime = Time.unscaledTime + inputCooldown; Navigate(1);  }
            else if (prev) { _nextInputTime = Time.unscaledTime + inputCooldown; Navigate(-1); }
            else if (down) { _nextInputTime = Time.unscaledTime + inputCooldown; NavigateVertical(1);  }
            else if (up)   { _nextInputTime = Time.unscaledTime + inputCooldown; NavigateVertical(-1); }
        }
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    private void Navigate(int dir)
    {
        int newIdx = _currentCol + dir;
        if (newIdx < 0 || newIdx >= CurrentRow.Count) return; // bounded — halt at edges
        StartCoroutine(SlideCarousel(dir));
    }

    // Switches to the phase row above/below. Bounded (no wrapping) — a single-row library
    // (only one phase ever played) makes both directions no-ops. Always lands on that
    // row's first motif, matching how a phase is actually entered during play.
    private void NavigateVertical(int dir)
    {
        int newRow = _currentRow + dir;
        if (newRow < 0 || newRow >= _rows.Count) return;

        _currentRow = newRow;
        _currentCol = 0;
        Refresh();
        UpdatePreviewForCenter();
    }

    private IEnumerator SlideCarousel(int dir)
    {
        _sliding = true;
        int n      = slots.Length;
        int center = n / 2;
        var row    = CurrentRow;

        // Only animate slots that are currently in-bounds.
        // Out-of-bounds slots are inactive and stay put.
        var toAnimate = new List<(int i, Vector3 start, Vector3 end)>();
        for (int i = 0; i < n; i++)
        {
            int dataIdx = _currentCol + (i - center);
            if (dataIdx < 0 || dataIdx >= row.Count) continue;
            toAnimate.Add((
                i:     i,
                start: _homePositions[i],
                end:   _homePositions[i] + Vector3.right * (-dir * _slotSpacing)
            ));
        }

        float elapsed = 0f;
        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / slideDuration));
            foreach (var (i, start, end) in toAnimate)
                slots[i].transform.position = Vector3.Lerp(start, end, t);
            yield return null;
        }

        // Snap animated slots to their end positions.
        foreach (var (i, _, end) in toAnimate)
            slots[i].transform.position = end;

        // Advance centre, reset all slots to home positions, rebuild.
        _currentCol += dir;
        for (int i = 0; i < n; i++)
            slots[i].transform.position = _homePositions[i];
        Refresh();

        _sliding = false;

        UpdatePreviewForCenter();
    }

    // ── Confirm ────────────────────────────────────────────────────────────────

    private void Confirm()
    {
        if (_confirming) return;
        _confirming = true;
        StartCoroutine(ConfirmRoutine());
    }

    private IEnumerator ConfirmRoutine()
    {
        // Fades the preview's own AudioSource to silence (see RingPreviewPlayer.StopAndFadeRoutine)
        // rather than guessing how long the synth's release tail needs — guarantees no residual
        // amplitude by the time the scene unload below destroys its MidiStreamPlayer mid-buffer.
        if (previewPlayer != null)
            yield return StartCoroutine(previewPlayer.StopAndFadeRoutine());

        if (_rows.Count == 0)
        {
            SceneManager.LoadScene(trackSelectionScene);
            yield break;
        }

        var entry = CurrentRow[_currentCol];
        PhaseLibraryStartConfig.RequestStart(entry.PhaseId, entry.PhaseIndex, entry.MotifIndex, entry.MotifId);
        onMotifConfirmed.Invoke();
        SceneManager.LoadScene(trackSelectionScene);
    }
}
