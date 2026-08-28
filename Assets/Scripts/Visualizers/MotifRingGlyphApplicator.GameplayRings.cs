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
    /// Called when the vehicle collides with this ring's collider. Breaks whichever pending note's
    /// preview tether landing point is closest to the collision contact point: retracts/destroys
    /// the tether, cancels its dot-launch, and removes the note from the loop so it's neither heard
    /// again nor included in the ring's eventual persisted record. Returns false (no-op) if no
    /// tether is currently pending.
    /// </summary>
    /// <remarks>
    /// _pendingRingNotes is shared across every bin/track currently mid-deformation (this
    /// applicator represents the whole ring stack behind one collider, not one ring each) — so
    /// picking by proximity to the actual contact point, rather than always the oldest entry,
    /// is what keeps a hit on one ring from breaking a note that belongs to a different bin.
    /// </remarks>
    public bool TryBreakNextPendingTether(Vector3 contactPoint)
    {
        if (_pendingRingNotes.Count == 0) return false;

        int bestIdx = 0;
        float bestDistSqr = float.MaxValue;
        for (int i = 0; i < _pendingRingNotes.Count; i++)
        {
            var anchor = _pendingRingNotes[i].Anchor;
            if (anchor == null) continue;
            float d = (anchor.transform.position - contactPoint).sqrMagnitude;
            if (d < bestDistSqr) { bestDistSqr = d; bestIdx = i; }
        }

        var entry = _pendingRingNotes[bestIdx];
        _pendingRingNotes.RemoveAt(bestIdx);
        entry.CancelFlag[0] = true;
        entry.OnCancelled?.Invoke();

        // Capture break-effect positions before the tether/anchor retract out from under them.
        Vector3? markerPos = entry.Tether != null && entry.Tether.start != null ? entry.Tether.start.position : (Vector3?)null;
        Vector3? anchorPos = entry.Anchor != null ? entry.Anchor.transform.position : (Vector3?)null;

        if (entry.Tether != null) entry.Tether.RetractAndDestroy(config.noteTetherGrowDuration);
        // Keep the anchor (the tether's 'end') alive through the retract animation — destroying
        // it now would null 'end' with no rebind info, tripping NoteTether's own "hopeless"
        // self-destruct on the next frame before the retract can play.
        if (entry.Anchor != null) Destroy(entry.Anchor, config.noteTetherGrowDuration);

        entry.Track?.RemovePersistentNoteAtStep(entry.Step);
        noteVisualizer?.RemoveCommittedMarkerAtStep(entry.Track, entry.Step);

        PlayBreakShake();
        if (tetherBreakEffectPrefab != null)
        {
            if (markerPos.HasValue) SpawnBreakEffect(markerPos.Value, entry.Track);
            if (anchorPos.HasValue) SpawnBreakEffect(anchorPos.Value, entry.Track);
        }

        return true;
    }

    /// <summary>
    /// Gracefully retracts every still-pending note tether (preview tethers whose notes haven't
    /// departed yet) instead of leaving them to the deformation coroutines' normal onDepart timing.
    /// Called right as the ring begins its end-of-motif spin so tethers detach in sync with the
    /// record turning, rather than lingering or vanishing abruptly. Unlike TryBreakNextPendingTether,
    /// this doesn't remove notes from the persistent loop or play break effects — the notes already
    /// completed; only their in-flight preview visuals are being wound down.
    /// </summary>
    public void DetachPendingRingTethers(float retractDuration)
    {
        if (_pendingRingNotes.Count == 0) return;

        foreach (var entry in _pendingRingNotes)
        {
            entry.CancelFlag[0] = true;
            entry.OnCancelled?.Invoke();
            if (entry.Tether != null) entry.Tether.RetractAndDestroy(retractDuration);
            if (entry.Anchor != null) Destroy(entry.Anchor, retractDuration);
        }
        _pendingRingNotes.Clear();
    }

    private void SpawnBreakEffect(Vector3 worldPos, InstrumentTrack track)
    {
        var go = Instantiate(tetherBreakEffectPrefab, worldPos, Quaternion.identity);
        var ps = go.GetComponentInChildren<ParticleSystem>();
        if (ps != null && track != null)
        {
            var main = ps.main;
            main.startColor = track.DisplayColor;
        }
    }

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

        EnsureRingCollider();
        _ringCollider.enabled = true;

        // A bin can empty (its last pending note broken via TryBreakNextPendingTether, which
        // un-fills the bin so BinFrontierAllocator offers it again) and later refill, re-firing
        // OnBinFilled → SpawnBinRing for the same (binIndex, role). Replace that stale entry in
        // place instead of appending a duplicate, so stacking radii stay consistent and
        // BeginBinRingDeformation's FindIndex targets the fresh ring instead of the old one.
        int existingIdx = _gameplayRings.FindIndex(r => r.BinIndex == binIndex && r.Role == role);
        int idx = existingIdx >= 0 ? existingIdx : _gameplayRings.Count;
        if (existingIdx >= 0 && _gameplayRings[existingIdx].Root != null)
            Destroy(_gameplayRings[existingIdx].Root);

        float innerR = RingInnerRadius(idx);
        float outerR = innerR + config.ringThickness;
        int   segs   = Mathf.Max(16, config.segments);

        var entry = BuildRingEntry($"GameplayRing_Bin{binIndex}_{role}",
            innerR, outerR, segs, color, role, binIndex,
            new List<MotifSnapshot.NoteEntry>(), totalSteps);
        if (existingIdx >= 0) _gameplayRings[existingIdx] = entry;
        else _gameplayRings.Add(entry);

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

        int totalRingCount = _gameplayRings.Count + _remainingRings.Count;
        RefreshPlayAreaFit(totalRingCount);
        UpdateRingColliderRadius(_gameplayRings.Count);
        UpdateBackgroundDiscRadii(_gameplayRings.Count, totalRingCount);
        EjectVehiclesFromRing();
    }

    // Guards against a vehicle getting fully engulfed the instant this collider is enabled or
    // grows to newly enclose it — Box2D's solver can't push out something that's entirely inside
    // a solid collider (only edge-overlap separations resolve reliably), so without this a
    // vehicle near center would stay trapped, and each stray contact while trapped would
    // incorrectly fire Vehicle.Damage.cs's ring-hit handler (TryBreakNextPendingTether).
    private void EjectVehiclesFromRing()
    {
        if (_ringCollider == null || !_ringCollider.enabled) return;
        var vehicles = GameFlowManager.Instance?.GetVehicles();
        if (vehicles == null) return;

        Vector2 center = _ringCollider.bounds.center;
        float   radius = _ringCollider.bounds.extents.x;
        foreach (var v in vehicles)
            if (v != null) v.EjectFromCircleIfInside(center, radius);
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
        // True only while a contour tween is actually in flight — NOT "every note has landed at
        // least once." A note broken via TryBreakNextPendingTether never reaches onLaunch, so if
        // every note in the bin ends up cancelled (trivially true for a 1-note bin), no tween
        // ever starts; this must default to "nothing to settle," not "permanently unsettled" —
        // the old inverted flag (start false, wait to become true) hung this coroutine forever
        // whenever a bin's only note(s) were all broken rather than landed.
        bool      contourTweenActive = false;
        int       cancelledCount = 0; // notes broken by the vehicle never reach onLaunch

        // shouldStop: only hard-clear kills deformation; spin-off lets it finish naturally.
        System.Func<bool> shouldStop = () => _clearingGameplayRings || ring.Root == null;

        foreach (var note in sortedNotes)
        {
            if (shouldStop()) break;

            int   localStep      = note.Step % binSteps;
            float angle          = localStep / (float)binSteps * Mathf.PI * 2f;
            var   tugLocal       = new Vector3(Mathf.Cos(angle) * tugR, Mathf.Sin(angle) * tugR, 0f);

            Transform markerTr = null;
            if (noteVisualizer?.noteMarkers != null && track != null)
                noteVisualizer.noteMarkers.TryGetValue((track, note.Step), out markerTr);

            // Preview tether: grows in immediately (all notes in the bin grow together, since
            // nothing in this loop yields) from the note's timeline marker to its future
            // landing spot on the ring. Retracts and self-destroys once the note's step is
            // reached (see onDepart below), in sync with its travel dot's departure/arrival.
            NoteTether tether = null;
            GameObject tetherAnchor = null;
            if (noteVisualizer?.noteTetherPrefab != null && track != null)
            {
                var ringLocalPos = new Vector3(Mathf.Cos(angle) * outerR, Mathf.Sin(angle) * outerR, 0f);
                tetherAnchor = new GameObject($"TetherAnchor_Bin{ring.BinIndex}_Step{note.Step}");
                tetherAnchor.transform.SetParent(ring.Root.transform, worldPositionStays: false);
                tetherAnchor.transform.localPosition = ringLocalPos;

                var tetherGo = Instantiate(noteVisualizer.noteTetherPrefab);
                tether = tetherGo.GetComponent<NoteTether>() ?? tetherGo.AddComponent<NoteTether>();
                tether.SetEndpoints(markerTr, tetherAnchor.transform, note.TrackColor);
                // The marker may not be registered in noteVisualizer.noteMarkers yet — this
                // resolves it immediately if it already exists, otherwise NoteTether polls for
                // it indefinitely (see NoteTether.Update()), so a lagging marker never silently
                // drops the preview the way a fixed-timeout wait here would.
                tether.BindStartByStep(track, note.Step, noteVisualizer);
                tether.GrowIn(config.noteTetherGrowDuration);
            }

            var capturedNote = note;
            var capturedTether = tether;
            var capturedAnchor = tetherAnchor;

            // While this note's tether is live (not yet departed), the vehicle can break it by
            // colliding with the ring — see TryBreakNextPendingTether.
            PendingRingNote pendingEntry = null;
            if (tether != null)
            {
                pendingEntry = new PendingRingNote
                {
                    Tether      = tether,
                    Anchor      = tetherAnchor,
                    Track       = track,
                    Step        = note.Step,
                    CancelFlag  = new bool[1],
                    OnCancelled = () => cancelledCount++,
                };
                _pendingRingNotes.Add(pendingEntry);
            }

            StartCoroutine(WaitAndLaunchDot(
                ring.Root.transform, tugLocal, note.TrackColor,
                markerTr, outerR, angle,
                drumRef, note.Step, leadSteps, config.noteTravelDuration, shouldStop,
                isCancelled: pendingEntry != null ? () => pendingEntry.CancelFlag[0] : null,
                onDepart: () =>
                {
                    if (pendingEntry != null) _pendingRingNotes.Remove(pendingEntry);
                    if (capturedTether != null) capturedTether.RetractAndDestroy(config.noteTravelDuration);
                    if (capturedAnchor != null) Destroy(capturedAnchor, config.noteTravelDuration);
                },
                onLaunch: () =>
                {
                    revealedNotes.Add(capturedNote);
                    var targetPoly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
                        role, ring.BinIndex, color, revealedNotes, binSteps, outerR, config);
                    var targetPts = targetPoly?.Points;
                    if (targetPts != null)
                    {
                        contourTweenActive = true;
                        if (contourTween != null) StopCoroutine(contourTween);
                        contourTween = StartCoroutine(
                            TweenContour(ring.Contour, targetPts, config.noteTravelDuration,
                                onComplete: () => contourTweenActive = false));
                    }
                }));
        }

        while (contourTweenActive || revealedNotes.Count + cancelledCount < sortedNotes.Count)
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

        if (_nextUpColorCoroutine != null)
        {
            StopCoroutine(_nextUpColorCoroutine);
            _nextUpColorCoroutine = null;
        }

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

        int totalRingCount = _gameplayRings.Count + _remainingRings.Count;
        RefreshPlayAreaFit(totalRingCount);
        UpdateRingColliderRadius(_gameplayRings.Count);
        UpdateBackgroundDiscRadii(_gameplayRings.Count, totalRingCount);
        EjectVehiclesFromRing();

        // Innermost not-yet-filled ring previews the motif's active role palette instead of
        // staying flat gray — static if the motif has one role, cycling if it has several.
        // Rebuilt every call, so completing a bin naturally hands the lerp off to the new
        // frontmost placeholder ring.
        if (_remainingRings.Count > 0)
        {
            var motif  = GameFlowManager.Instance?.phaseTransitionManager?.currentMotif;
            var colors = motif?.GetActiveRoleColors();
            _nextUpColorCoroutine = StartCoroutine(AnimateNextUpRingColorCycle(_remainingRings[0], colors));
        }
    }

    private IEnumerator AnimateNextUpRingColorCycle(RingEntry ring, List<Color> colors)
    {
        if (colors == null || colors.Count == 0 || ring.Root == null) yield break;

        if (colors.Count == 1)
        {
            ApplyNextUpColor(ring, colors[0]);
            yield break;
        }

        var mpb = new MaterialPropertyBlock();
        int i = 0;
        while (ring.Root != null)
        {
            Color from = colors[i];
            Color to   = colors[(i + 1) % colors.Count];
            float dur  = Mathf.Max(0.01f, config.nextUpColorCycleDuration);
            float t    = 0f;
            while (t < dur && ring.Root != null)
            {
                t += Time.deltaTime;
                ApplyNextUpColor(ring, Color.Lerp(from, to, t / dur), mpb);
                yield return null;
            }
            i = (i + 1) % colors.Count;
        }
    }

    private void ApplyNextUpColor(RingEntry ring, Color baseColor, MaterialPropertyBlock mpb = null)
    {
        _nextUpCurrentColor = baseColor;

        if (ring.Fill != null)
            SetFillColor(ring.Fill,
                new Color(baseColor.r, baseColor.g, baseColor.b, config.remainingRingAlpha),
                mpb ?? new MaterialPropertyBlock());

        if (ring.Contour != null)
        {
            var lc = new Color(baseColor.r, baseColor.g, baseColor.b, config.remainingContourAlpha);
            ring.Contour.startColor = lc;
            ring.Contour.endColor   = lc;
        }
    }

    /// <summary>Destroy all gameplay rings immediately and hide the record.</summary>
    public void ClearGameplayRings()
    {
        StopAllCoroutines(); // prevent ghost decrements from old deformation coroutines
        _shakeCoroutine = null; // StopAllCoroutines above killed it without resetting localPosition
        transform.localPosition = Vector3.zero;
        _clearingGameplayRings   = true;
        _gameplayFadingOut       = true;
        _superNodeMode           = false;
        _spinOffPending          = false;
        _pendingDeformationCount = 0;
        _pendingRingNotes.Clear();
        DestroyList(_gameplayRings);
        DestroyList(_remainingRings);
        _remainingRings.Clear();
        if (_fullBgDisc != null)   { Destroy(_fullBgDisc.gameObject);   _fullBgDisc = null; }
        if (_placedBgDisc != null) { Destroy(_placedBgDisc.gameObject); _placedBgDisc = null; }
        _gameplayFadingOut       = false;
        _clearingGameplayRings   = false;
        transform.localScale     = Vector3.zero;
        if (_ringCollider != null) _ringCollider.enabled = false;
    }

    /// <summary>
    /// Fade the entire initial record (placed + remaining placeholder rings, both
    /// background discs) to invisible, then tear the stack down. Used for the
    /// intro reveal at motif start — either synced to maze-generation duration
    /// (first motif) or on the config default (every other motif transition,
    /// where the new maze is already fully generated by the time this runs).
    /// </summary>
    public void FadeRecordOut(float? duration = null)
    {
        if (config == null) return;
        if (_gameplayHideCoroutine != null)
        {
            StopCoroutine(_gameplayHideCoroutine);
            _gameplayHideCoroutine = null;
        }
        if (_recordFadeOutCoroutine != null) StopCoroutine(_recordFadeOutCoroutine);
        _recordFadeOutCoroutine = StartCoroutine(FadeRecordOutRoutine(duration ?? config.recordFadeOutDuration));
    }

    private IEnumerator FadeRecordOutRoutine(float duration)
    {
        if (_nextUpColorCoroutine != null)
        {
            StopCoroutine(_nextUpColorCoroutine);
            _nextUpColorCoroutine = null;
        }

        int total = _gameplayRings.Count + _remainingRings.Count;
        if (total == 0) { _recordFadeOutCoroutine = null; yield break; }

        var mpbs = MakeMpbs(total);
        float dur = Mathf.Max(0.01f, duration);
        float t   = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float k = 1f - Mathf.Clamp01(t / dur);
            int idx = 0;
            for (int i = 0; i < _gameplayRings.Count; i++, idx++)
                FadeEntry(_gameplayRings[i], _gameplayRings[i].BaseColor,
                    config.ringAlpha * k, config.contourAlpha * k, mpbs[idx]);
            for (int i = 0; i < _remainingRings.Count; i++, idx++)
            {
                var rgb = (i == 0) ? _nextUpCurrentColor : _remainingRings[i].BaseColor;
                FadeEntry(_remainingRings[i], rgb,
                    config.remainingRingAlpha * k, config.remainingContourAlpha * k, mpbs[idx]);
            }
            FadeBackgroundDiscs(k);
            yield return null;
        }

        ClearGameplayRings(); // also tears down the two background discs
        _recordFadeOutCoroutine = null;
    }

    private void FadeEntry(RingEntry ring, Color rgb, float fillAlpha, float contourAlpha, MaterialPropertyBlock mpb)
    {
        if (ring.Fill != null)
            SetFillColor(ring.Fill, new Color(rgb.r, rgb.g, rgb.b, fillAlpha), mpb);
        if (ring.Contour != null)
        {
            var lc = new Color(rgb.r, rgb.g, rgb.b, contourAlpha);
            ring.Contour.startColor = lc;
            ring.Contour.endColor   = lc;
        }
    }

    private void FadeBackgroundDiscs(float k)
    {
        if (_placedBgDisc != null)
            SetFillColor(_placedBgDisc,
                new Color(config.backgroundDiscColor.r, config.backgroundDiscColor.g, config.backgroundDiscColor.b, config.placedBackgroundAlpha * k),
                _placedBgMpb);
        if (_fullBgDisc != null)
            SetFillColor(_fullBgDisc,
                new Color(config.backgroundDiscColor.r, config.backgroundDiscColor.g, config.backgroundDiscColor.b, config.fullBackgroundAlpha * k),
                _fullBgMpb);
    }
}
