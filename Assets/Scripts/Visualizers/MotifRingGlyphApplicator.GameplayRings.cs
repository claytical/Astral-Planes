using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ── Gameplay ring API ────────────────────────────────────────────────────
public partial class MotifRingGlyphApplicator
{
    // Called by SuperNodeTrackNode.Collect() before InstantFillAllBins fires OnBinFilled.
    // Ring spawning for the new bins happens via the OnBinFilled event path — not via any
    // explicit SpawnBinRing loop here.
    public void SetSuperNodeMode(bool active) => _superNodeMode = active;

    /// <summary>
    /// Spawn one flat circle ring for a just-completed bin. Deformation fires
    /// separately via <see cref="BeginBinRingDeformation"/> when the playhead
    /// reaches the bin's start step.
    /// </summary>
    public void SpawnBinRing(MusicalRole role, int binIndex, Color color,
                              List<MotifSnapshot.NoteEntry> notes, int totalSteps,
                              InstrumentTrack track = null)
    {
        if (config == null) return;

        int   idx    = _gameplayRings.Count;
        float innerR = RingInnerRadius(idx);
        float outerR = innerR + config.ringThickness;
        int   segs   = Mathf.Max(16, config.segments);

        var entry = BuildRingEntry($"GameplayRing_Bin{binIndex}_{role}",
            innerR, outerR, segs, color, role, binIndex,
            new List<MotifSnapshot.NoteEntry>(), totalSteps);
        _gameplayRings.Add(entry);

        var   drum            = GameFlowManager.Instance?.activeDrumTrack;
        float stepDurationSec = 0f;
        drum?.TryGetNextBaseStepDsp(out _, out stepDurationSec);
        int   drumTotalSteps  = drum != null ? drum.totalSteps : totalSteps;
        float binDurationSec  = drumTotalSteps * stepDurationSec;
        float rotDeg          = binDurationSec > 0.01f ? 360f / binDurationSec : 120f;
        if (idx % 2 == 1) rotDeg = -rotDeg;

        StartCoroutine(AnimateMeshFill(
            entry.Fill.GetComponent<MeshFilter>().sharedMesh,
            entry.FullTris, segs, delay: 0f, config.ringAppearDuration));

        // Draw in the flat circle contour, then rotate persistently.
        StartCoroutine(AnimateSingleRing(
            entry.Contour, entry.ContourPoints,
            0f, config.ringAppearDuration,
            entry.Root.transform, rotDeg,
            new List<NoteAnimInfo>(), null,
            shouldStop: () => _gameplayFadingOut));

        // Reset deformation count when the record was hidden — starts a fresh session.
        if (transform.localScale.sqrMagnitude < 0.0001f)
            _pendingDeformationCount = 0;

        RefreshPlayAreaFit(_gameplayRings.Count + _remainingRings.Count);
    }

    /// <summary>
    /// Fire the drum-step-synced deformation sequence for the ring that was
    /// spawned for <paramref name="binIndex"/>. Called by BinRingController
    /// when the playhead reaches the bin's start step.
    /// </summary>
    public void BeginBinRingDeformation(int binIndex, List<MotifSnapshot.NoteEntry> notes,
        int totalSteps, InstrumentTrack track, MusicalRole role, Color color)
    {
        // Match on both binIndex AND role — multiple roles can share the same binIndex.
        int ringIdx = _gameplayRings.FindIndex(r => r.BinIndex == binIndex && r.Role == role);
        if (ringIdx < 0 || config == null) return;

        // A new bin is starting to deform — cancel any leftover hide/scale-down from a
        // previous bin so it can't shrink the stack out from under these incoming notes.
        if (_gameplayHideCoroutine != null)
        {
            StopCoroutine(_gameplayHideCoroutine);
            _gameplayHideCoroutine = null;
        }

        float innerR = RingInnerRadius(ringIdx);
        float outerR = innerR + config.ringThickness;
        _pendingDeformationCount++;
        StartCoroutine(DeformBinRingCoroutine(
            _gameplayRings[ringIdx], notes, totalSteps, track, role, color, outerR));
    }

    private IEnumerator DeformBinRingCoroutine(
        RingEntry ring, List<MotifSnapshot.NoteEntry> notes,
        int totalSteps, InstrumentTrack track, MusicalRole role, Color color, float outerR)
    {
        if (ring.Root == null) { _pendingDeformationCount--; yield break; }

        int   binSteps    = Mathf.Max(1, totalSteps);
        var   drumRef     = GameFlowManager.Instance?.activeDrumTrack;
        int   leaderSteps = drumRef != null ? drumRef.GetLeaderSteps() : binSteps;

        float stepDur = 0f;
        drumRef?.TryGetNextBaseStepDsp(out _, out stepDur);
        int leadSteps = stepDur > 0.001f
            ? Mathf.Max(1, Mathf.RoundToInt(config.noteTravelDuration / stepDur))
            : FallbackLeadSteps;

        float tugR = outerR * (1f - config.tugDepthFraction);

        var sortedNotes = notes == null
            ? new List<MotifSnapshot.NoteEntry>()
            : notes.OrderBy(n => n.Step % leaderSteps).ToList();

        var      revealedNotes = new List<MotifSnapshot.NoteEntry>();
        Coroutine contourTween = null;
        bool      contourSettled = sortedNotes.Count == 0;

        // shouldStop: only hard-clear kills deformation; spin-off lets it finish naturally.
        System.Func<bool> shouldStop = () => _clearingGameplayRings || ring.Root == null;

        foreach (var note in sortedNotes)
        {
            if (shouldStop()) break;

            int   noteLeaderStep = note.Step % leaderSteps;
            int   triggerStep    = (noteLeaderStep - leadSteps + leaderSteps) % leaderSteps;
            int   localStep      = note.Step % binSteps;
            float angle          = localStep / (float)binSteps * Mathf.PI * 2f;
            var   tugLocal       = new Vector3(Mathf.Cos(angle) * tugR, Mathf.Sin(angle) * tugR, 0f);

            Transform markerTr = null;
            if (noteVisualizer?.noteMarkers != null && track != null)
                noteVisualizer.noteMarkers.TryGetValue((track, note.Step), out markerTr);

            var capturedNote = note;
            StartCoroutine(WaitAndLaunchDot(
                ring.Root.transform, tugLocal, note.TrackColor,
                markerTr, outerR, angle,
                drumRef, triggerStep, config.noteTravelDuration, shouldStop,
                onLaunch: () =>
                {
                    revealedNotes.Add(capturedNote);
                    var targetPoly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
                        role, ring.BinIndex, color, revealedNotes, binSteps, outerR, config);
                    var targetPts = targetPoly?.Points;
                    if (targetPts != null)
                    {
                        contourSettled = false;
                        if (contourTween != null) StopCoroutine(contourTween);
                        contourTween = StartCoroutine(
                            TweenContour(ring.Contour, targetPts, config.noteTravelDuration,
                                onComplete: () => contourSettled = true));
                    }
                }));
        }

        while (!contourSettled || revealedNotes.Count < sortedNotes.Count)
        {
            if (shouldStop()) { _pendingDeformationCount--; yield break; }
            yield return null;
        }

        _pendingDeformationCount--;

        // Hide the stack after deformation — only when all concurrent deformations are done
        // and we are not in spin-off or SuperNode mode. Runs as its own coroutine so a newly
        // started bin's deformation can cancel it outright (see BeginBinRingDeformation).
        if (!_superNodeMode && !_gameplayFadingOut && !_clearingGameplayRings && !_spinOffPending
            && _pendingDeformationCount == 0)
        {
            _gameplayHideCoroutine = StartCoroutine(HideGameplayRingStackAfterLoopBoundary());
        }
    }

    // Waits for the drum's next full loop boundary (so the stack stays visible through the
    // remainder of the currently-playing loop), then scales the whole ring stack to zero.
    // Bails at any point if a new bin's deformation starts (_pendingDeformationCount > 0) or if
    // BeginBinRingDeformation cancels this coroutine directly.
    private IEnumerator HideGameplayRingStackAfterLoopBoundary()
    {
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum != null)
        {
            bool boundaryHit = false;
            System.Action onBoundary = () => boundaryHit = true;
            drum.OnLoopBoundary += onBoundary;
            while (!boundaryHit && !_superNodeMode && !_gameplayFadingOut
                   && !_clearingGameplayRings && !_spinOffPending && _pendingDeformationCount == 0)
                yield return null;
            drum.OnLoopBoundary -= onBoundary;
        }

        if (_superNodeMode || _gameplayFadingOut || _clearingGameplayRings || _spinOffPending
            || _pendingDeformationCount != 0)
        {
            _gameplayHideCoroutine = null;
            yield break;
        }

        float dur          = config != null ? config.scaleDownDuration : 0.5f;
        Vector3 startScale = transform.localScale;
        float elapsed      = 0f;
        while (elapsed < dur && !_clearingGameplayRings && !_gameplayFadingOut
               && _pendingDeformationCount == 0)
        {
            elapsed += Time.deltaTime;
            transform.localScale = Vector3.Lerp(startScale, Vector3.zero, elapsed / dur);
            yield return null;
        }
        if (!_clearingGameplayRings && !_gameplayFadingOut && _pendingDeformationCount == 0)
            transform.localScale = Vector3.zero;

        _gameplayHideCoroutine = null;
    }

    /// <summary>
    /// Show gray/diminished placeholder rings for motif progress not yet completed.
    /// Rebuilds the placeholder stack each call (called once per Collectable set
    /// finishing its landing on the timeline), stacked just outside the current
    /// completed (_gameplayRings) count.
    /// </summary>
    public void SetRemainingProgressRings(int remainingCount)
    {
        if (config == null) return;

        DestroyList(_remainingRings);
        _remainingRings.Clear();

        remainingCount = Mathf.Max(0, remainingCount);
        int segs = Mathf.Max(16, config.segments);

        for (int i = 0; i < remainingCount; i++)
        {
            int   idx    = _gameplayRings.Count + i;
            float innerR = RingInnerRadius(idx);
            float outerR = innerR + config.ringThickness;

            var entry = BuildRingEntry($"RemainingRing_{i}", innerR, outerR, segs,
                config.remainingRingColor, default, -1,
                new List<MotifSnapshot.NoteEntry>(), 0,
                config.remainingRingAlpha, config.remainingContourAlpha);
            _remainingRings.Add(entry);

            StartCoroutine(AnimateMeshFill(
                entry.Fill.GetComponent<MeshFilter>().sharedMesh,
                entry.FullTris, segs, delay: i * config.ringStaggerDelay, config.ringAppearDuration));

            StartCoroutine(AnimateSingleRing(
                entry.Contour, entry.ContourPoints,
                i * config.ringStaggerDelay, config.ringAppearDuration,
                entry.Root.transform, 0f,
                new List<NoteAnimInfo>(), null,
                shouldStop: () => _gameplayFadingOut));
        }

        RefreshPlayAreaFit(_gameplayRings.Count + _remainingRings.Count);
    }

    /// <summary>Destroy all gameplay rings immediately and hide the record.</summary>
    public void ClearGameplayRings()
    {
        StopAllCoroutines(); // prevent ghost decrements from old deformation coroutines
        _clearingGameplayRings   = true;
        _gameplayFadingOut       = true;
        _superNodeMode           = false;
        _spinOffPending          = false;
        _pendingDeformationCount = 0;
        DestroyList(_gameplayRings);
        DestroyList(_remainingRings);
        _remainingRings.Clear();
        _gameplayFadingOut       = false;
        _clearingGameplayRings   = false;
        transform.localScale     = Vector3.zero;
    }
}
