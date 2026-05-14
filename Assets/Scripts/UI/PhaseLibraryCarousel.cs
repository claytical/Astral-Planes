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
/// Bounded (no wrapping):
///   - Slots whose data index falls outside [0, rings.Count-1] are deactivated.
///   - Navigation halts at the edges.
///
/// No rings saved:
///   - All slots hidden; pressToStartCue shown.
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

    /// <summary>Fired when the player confirms a motif, before the scene loads.</summary>
    public UnityEvent onMotifConfirmed;

    private List<MotifSnapshot> _rings = new();
    private int   _centerDataIndex;
    private float _slotSpacing;
    private bool  _sliding;
    private float _nextInputTime;
    private Vector3[] _homePositions;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start()
    {
        _rings = RingSessionStore.LoadAllRingsFromDisk();
        _rings.Sort((a, b) => a.PhaseIndex != b.PhaseIndex
            ? a.PhaseIndex.CompareTo(b.PhaseIndex)
            : a.MotifIndex.CompareTo(b.MotifIndex));

        InitSlotLayout();

        bool hasRings = _rings.Count > 0;
        if (pressToStartCue != null) pressToStartCue.SetActive(!hasRings);

        if (hasRings)
        {
            Refresh();
            if (previewPlayer != null)
                previewPlayer.Play(_rings[_centerDataIndex]);
        }
        else
        {
            foreach (var s in slots) s.gameObject.SetActive(false);
        }
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
            bool inBounds = dataIdx >= 0 && dataIdx < _rings.Count;

            slots[i].gameObject.SetActive(inBounds);
            if (!inBounds) continue;

            if (i == center) slots[i].AnimateApply(_rings[dataIdx]);
            else             slots[i].ApplyStatic(_rings[dataIdx], unselectedAlpha);
        }
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
            confirm |= gp.buttonSouth.wasPressedThisFrame || gp.buttonEast.wasPressedThisFrame;
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            next    |= kb.rightArrowKey.wasPressedThisFrame;
            prev    |= kb.leftArrowKey.wasPressedThisFrame;
            confirm |= kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame;
        }

        if (_rings.Count == 0)
        {
            if (confirm) SceneManager.LoadScene(trackSelectionScene);
            return;
        }

        if (confirm) { Confirm(); return; }

        if (!_sliding && Time.unscaledTime >= _nextInputTime)
        {
            if      (next) { _nextInputTime = Time.unscaledTime + inputCooldown; Navigate(1);  }
            else if (prev) { _nextInputTime = Time.unscaledTime + inputCooldown; Navigate(-1); }
        }
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    private void Navigate(int dir)
    {
        int newIdx = _centerDataIndex + dir;
        if (newIdx < 0 || newIdx >= _rings.Count) return; // bounded — halt at edges
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
            if (dataIdx < 0 || dataIdx >= _rings.Count) continue;
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

        if (previewPlayer != null)
            previewPlayer.Play(_rings[_centerDataIndex]);
    }

    // ── Confirm ────────────────────────────────────────────────────────────────

    private void Confirm()
    {
        if (previewPlayer != null)
            previewPlayer.Stop();

        if (_rings.Count == 0)
        {
            SceneManager.LoadScene(trackSelectionScene);
            return;
        }

        var snap = _rings[_centerDataIndex];
        PhaseLibraryStartConfig.RequestStart(snap.PhaseIndex, snap.MotifIndex);
        onMotifConfirmed.Invoke();
        SceneManager.LoadScene(trackSelectionScene);
    }
}
