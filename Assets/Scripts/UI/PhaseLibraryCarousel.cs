using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Bounded carousel for selecting a saved motif ring in the Main scene.
///
/// Slots are fixed world-space positions sorted left-to-right.
/// Centre index = slots.Length / 2.
///
/// Entries are either a played motif (has a saved MotifSnapshot — audible, full ring detail)
/// or the single "next up" locked preview: the first not-yet-completed motif in PhaseLibrary
/// order, rendered as gray/unlit rings (count = nodesPerStar) via ApplyLockedPreview. It's
/// selectable — confirming it jumps straight into that motif — but never previewed with audio.
///
/// Bounded (no wrapping):
///   - Slots whose data index falls outside [0, entries.Count-1] are deactivated.
///   - Navigation halts at the edges.
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

    [Tooltip("Caps each slot so no ring exceeds this world-space radius. Matches SolarSystemRecordDisplay.")]
    [SerializeField] private float maxRecordRadius = 0.5f;

    [Tooltip("Shown when no ring records exist yet. Hide when rings are present.")]
    [SerializeField] private GameObject pressToStartCue;

    [Tooltip("Plays audio preview of the centered ring.")]
    [SerializeField] private RingPreviewPlayer previewPlayer;

    [Tooltip("Authoritative motif list, used to find the next not-yet-completed motif to offer as a locked preview.")]
    [SerializeField] private PhaseLibrary phaseLibrary;

    /// <summary>Fired when the player confirms a motif, before the scene loads.</summary>
    public UnityEvent onMotifConfirmed;

    /// <summary>One carousel slot's worth of data: either a recorded snapshot or a not-yet-played motif offered as a locked preview.</summary>
    private struct CarouselEntry
    {
        public MotifSnapshot Snapshot;   // non-null for a played/recorded motif
        public MotifProfile  Profile;    // non-null only for the locked-preview entry
        public int    PhaseIndex;
        public int    MotifIndex;
        public string MotifId;
        public bool   IsLocked => Snapshot == null;
    }

    private List<CarouselEntry> _entries = new();
    private int   _centerDataIndex;
    private float _slotSpacing;
    private bool  _sliding;
    private float _nextInputTime;
    private Vector3[] _homePositions;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start()
    {
        var rings = RingSessionStore.LoadAllRingsFromDisk();

        _entries = rings.Select(r => new CarouselEntry
        {
            Snapshot   = r,
            PhaseIndex = r.PhaseIndex,
            MotifIndex = r.MotifIndex,
            MotifId    = r.MotifId,
        }).ToList();

        if (_entries.Count > 0 && TryFindNextUnplayedMotif(rings, out var lockedEntry))
            _entries.Add(lockedEntry);

        _entries.Sort((a, b) => a.PhaseIndex != b.PhaseIndex
            ? a.PhaseIndex.CompareTo(b.PhaseIndex)
            : a.MotifIndex.CompareTo(b.MotifIndex));

        InitSlotLayout();

        bool hasEntries = _entries.Count > 0;
        if (pressToStartCue != null) pressToStartCue.SetActive(!hasEntries);

        if (hasEntries)
        {
            Refresh();
            UpdatePreviewForCenter();
        }
        else
        {
            foreach (var s in slots) s.gameObject.SetActive(false);
        }
    }

    // Scans the authored PhaseLibrary in (phaseIndex, motifIndex) order and returns the first
    // motif with no matching saved snapshot — i.e. the next one the player hasn't completed yet.
    private bool TryFindNextUnplayedMotif(List<MotifSnapshot> rings, out CarouselEntry entry)
    {
        entry = default;
        if (phaseLibrary == null || phaseLibrary.phases == null) return false;

        for (int p = 0; p < phaseLibrary.phases.Count; p++)
        {
            var phase = phaseLibrary.phases[p];
            if (phase?.motifs == null) continue;
            for (int m = 0; m < phase.motifs.Count; m++)
            {
                var motif = phase.motifs[m];
                if (motif == null) continue;
                bool played = rings.Exists(r => r.PhaseIndex == p && r.MotifIndex == m);
                if (played) continue;

                entry = new CarouselEntry
                {
                    Snapshot   = null,
                    Profile    = motif,
                    PhaseIndex = p,
                    MotifIndex = m,
                    MotifId    = motif.motifId,
                };
                return true;
            }
        }
        return false;
    }

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
        int center = slots.Length / 2;
        for (int i = 0; i < slots.Length; i++)
        {
            int  dataIdx  = _centerDataIndex + (i - center);
            bool inBounds = dataIdx >= 0 && dataIdx < _entries.Count;

            slots[i].gameObject.SetActive(inBounds);
            if (!inBounds) continue;

            slots[i].maxDisplayRadius = maxRecordRadius;
            var entry = _entries[dataIdx];
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
        var entry = _entries[_centerDataIndex];
        if (entry.IsLocked) previewPlayer.Stop();
        else                previewPlayer.Play(entry.Snapshot);
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    private void Update()
    {
        bool next = false, prev = false, confirm = false;

        var gp = Gamepad.current;
        if (gp != null)
        {
            next    |= gp.dpad.right.wasPressedThisFrame;
            prev    |= gp.dpad.left.wasPressedThisFrame;
            confirm |= gp.buttonSouth.wasPressedThisFrame;
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            next    |= kb.rightArrowKey.wasPressedThisFrame;
            prev    |= kb.leftArrowKey.wasPressedThisFrame;
            confirm |= kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame;
        }

        if (_entries.Count == 0)
        {
            if (confirm) SceneManager.LoadScene(trackSelectionScene);
            return;
        }

        if (_sliding) return; // ignore all input mid-slide so Confirm() never reads a stale _centerDataIndex

        if (confirm) { Confirm(); return; }

        if (Time.unscaledTime >= _nextInputTime)
        {
            if      (next) { _nextInputTime = Time.unscaledTime + inputCooldown; Navigate(1);  }
            else if (prev) { _nextInputTime = Time.unscaledTime + inputCooldown; Navigate(-1); }
        }
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    private void Navigate(int dir)
    {
        int newIdx = _centerDataIndex + dir;
        if (newIdx < 0 || newIdx >= _entries.Count) return; // bounded — halt at edges
        StartCoroutine(SlideCarousel(dir));
    }

    private IEnumerator SlideCarousel(int dir)
    {
        _sliding = true;
        int n      = slots.Length;
        int center = n / 2;

        // Only animate slots that are currently in-bounds.
        // Out-of-bounds slots are inactive and stay put.
        var toAnimate = new List<(int i, Vector3 start, Vector3 end)>();
        for (int i = 0; i < n; i++)
        {
            int dataIdx = _centerDataIndex + (i - center);
            if (dataIdx < 0 || dataIdx >= _entries.Count) continue;
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
        _centerDataIndex += dir;
        for (int i = 0; i < n; i++)
            slots[i].transform.position = _homePositions[i];
        Refresh();

        _sliding = false;

        UpdatePreviewForCenter();
    }

    // ── Confirm ────────────────────────────────────────────────────────────────

    private void Confirm()
    {
        if (previewPlayer != null)
            previewPlayer.Stop();

        if (_entries.Count == 0)
        {
            SceneManager.LoadScene(trackSelectionScene);
            return;
        }

        var entry = _entries[_centerDataIndex];
        PhaseLibraryStartConfig.RequestStart(entry.PhaseIndex, entry.MotifIndex, entry.MotifId);
        onMotifConfirmed.Invoke();
        SceneManager.LoadScene(trackSelectionScene);
    }
}
