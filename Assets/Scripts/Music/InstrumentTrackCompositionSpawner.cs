using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Composition-mode burst spawning: turns a NoteSet into a step-sequenced queue of
/// collectable launches, resolves each collectable's destination grid cell, and fires
/// them one at a time as the drum transport reaches their authored step. Owns the
/// pending-launch queue, the step-listener subscription, and the one-shot spawn-effect
/// particle instance. Extracted from InstrumentTrack.cs; talks back to the host only
/// through the delegates passed at construction, mirroring the CosmicDustGenerator
/// collaborator pattern (see CosmicDustCellRegistry.cs).
/// </summary>
public sealed class InstrumentTrackCompositionSpawner
{
    private struct PendingCompositionLaunch
    {
        public int absStep;
        public int note;
        public int duration;
        public float velocity127;
        public Vector3 originWorld;
        public Vector3 targetWorld;
        public Vector2Int targetCell;
        public bool cellHasDust;
        public NoteSet noteSet;
        public int burstId;
    }

    private readonly List<PendingCompositionLaunch> _pendingCompositionLaunches = new();
    private bool _compositionStepListenerActive;
    private ParticleSystem _compositionSpawnEffect;
    private int _nextBurstId;
    private readonly List<int> _scratchSteps = new List<int>(1);

    private readonly Func<string> _getName;
    private readonly Func<GameObject> _getCollectablePrefab;
    private readonly Func<Transform> _getCollectableParent;
    private readonly Func<LayerMask> _getSpawnBlockedMask;
    private readonly Func<ParticleSystem> _getCompositionSpawnEffectPrefab;
    private readonly Func<Color> _getDisplayColor;
    private readonly Func<Vector3> _getHostPosition;
    private readonly Func<int> _getLoopMultiplier;
    private readonly Func<InstrumentTrackController> _getController;
    private readonly Func<DrumTrack> _getDrumTrack;
    private readonly Func<TrackExpansionController> _getExpansionCtrl;
    private readonly Func<int> _getCurrentBurstId;
    private readonly Action<int> _setCurrentBurstId;
    private readonly Action<bool> _setCurrentBurstArmed;
    private readonly Action<int> _setCurrentBurstRemaining;
    private readonly Func<int, bool> _hasAnyNoteInBin;
    private readonly Func<int> _getNextBinForSpawn;
    private readonly Action<int, bool> _setBinAllocated;
    private readonly Action<int> _advanceCursorPastBin;
    private readonly Func<int> _getBinSize;
    private readonly Action<Collectable> _hookCollectableDestroyHandler;
    private readonly Action<NoteVisualizer, Collectable, int, int> _placeAndBindPlaceholderMarker;
    private readonly Action<int, float, int> _playOneShotMidi;
    private readonly Action<int, int, int> _registerBurstState; // (burstId, queuedCount, targetBin)
    private readonly Func<int, int> _getBurstTargetBinOrZero;
    private readonly Action<int, bool> _raiseBurstCleared;
    private readonly Func<int> _resolveTargetBinFromController; // () => controller?.GetBinForNextSpawn(host) ?? GetNextBinForSpawn()
    private readonly Func<bool> _isExpansionPending;
    private readonly Action<Vector3> _beginGravityVoidForPendingExpandIfEligible;
    private readonly Action<Collectable> _assignInstrumentTrack; // c => c.assignedInstrumentTrack = this
    private readonly Action<Collectable> _applyTrackVisuals; // c => c.ApplyTrackVisuals(this)
    private readonly Action<Collectable, Vector3, Vector3, int, int, NoteSet, List<int>> _beginSpawnArrival;
    private readonly Func<NoteSet, int, int> _getNoteForPhaseAndRole; // (noteSet, step) => noteSet.GetNoteForPhaseAndRole(host, step)
    private readonly Action _canonicalizeOwnMarkers; // () => noteVisualizer?.CanonicalizeTrackMarkers(host, currentBurstId)
    private readonly List<GameObject> _spawnedCollectables;

    public InstrumentTrackCompositionSpawner(
        Func<string> getName,
        Func<GameObject> getCollectablePrefab, Func<Transform> getCollectableParent,
        Func<LayerMask> getSpawnBlockedMask, Func<ParticleSystem> getCompositionSpawnEffectPrefab,
        Func<Color> getDisplayColor, Func<Vector3> getHostPosition,
        Func<int> getLoopMultiplier,
        Func<InstrumentTrackController> getController, Func<DrumTrack> getDrumTrack,
        Func<TrackExpansionController> getExpansionCtrl,
        Func<int> getCurrentBurstId, Action<int> setCurrentBurstId,
        Action<bool> setCurrentBurstArmed, Action<int> setCurrentBurstRemaining,
        Func<int, bool> hasAnyNoteInBin, Func<int> getNextBinForSpawn,
        Action<int, bool> setBinAllocated, Action<int> advanceCursorPastBin, Func<int> getBinSize,
        Action<Collectable> hookCollectableDestroyHandler,
        Action<NoteVisualizer, Collectable, int, int> placeAndBindPlaceholderMarker,
        Action<int, float, int> playOneShotMidi,
        Action<int, int, int> registerBurstState,
        Func<int, int> getBurstTargetBinOrZero,
        Action<int, bool> raiseBurstCleared,
        Func<int> resolveTargetBinFromController,
        Func<bool> isExpansionPending,
        Action<Vector3> beginGravityVoidForPendingExpandIfEligible,
        Action<Collectable> assignInstrumentTrack,
        Action<Collectable> applyTrackVisuals,
        Action<Collectable, Vector3, Vector3, int, int, NoteSet, List<int>> beginSpawnArrival,
        Func<NoteSet, int, int> getNoteForPhaseAndRole,
        Action canonicalizeOwnMarkers,
        List<GameObject> spawnedCollectables)
    {
        _getName = getName;
        _getCollectablePrefab = getCollectablePrefab;
        _getCollectableParent = getCollectableParent;
        _getSpawnBlockedMask = getSpawnBlockedMask;
        _getCompositionSpawnEffectPrefab = getCompositionSpawnEffectPrefab;
        _getDisplayColor = getDisplayColor;
        _getHostPosition = getHostPosition;
        _getLoopMultiplier = getLoopMultiplier;
        _getController = getController;
        _getDrumTrack = getDrumTrack;
        _getExpansionCtrl = getExpansionCtrl;
        _getCurrentBurstId = getCurrentBurstId;
        _setCurrentBurstId = setCurrentBurstId;
        _setCurrentBurstArmed = setCurrentBurstArmed;
        _setCurrentBurstRemaining = setCurrentBurstRemaining;
        _hasAnyNoteInBin = hasAnyNoteInBin;
        _getNextBinForSpawn = getNextBinForSpawn;
        _setBinAllocated = setBinAllocated;
        _advanceCursorPastBin = advanceCursorPastBin;
        _getBinSize = getBinSize;
        _hookCollectableDestroyHandler = hookCollectableDestroyHandler;
        _placeAndBindPlaceholderMarker = placeAndBindPlaceholderMarker;
        _playOneShotMidi = playOneShotMidi;
        _registerBurstState = registerBurstState;
        _getBurstTargetBinOrZero = getBurstTargetBinOrZero;
        _raiseBurstCleared = raiseBurstCleared;
        _resolveTargetBinFromController = resolveTargetBinFromController;
        _isExpansionPending = isExpansionPending;
        _beginGravityVoidForPendingExpandIfEligible = beginGravityVoidForPendingExpandIfEligible;
        _assignInstrumentTrack = assignInstrumentTrack;
        _applyTrackVisuals = applyTrackVisuals;
        _beginSpawnArrival = beginSpawnArrival;
        _getNoteForPhaseAndRole = getNoteForPhaseAndRole;
        _canonicalizeOwnMarkers = canonicalizeOwnMarkers;
        _spawnedCollectables = spawnedCollectables;
    }

    public void SpawnBurst(
        NoteSet noteSet,
        int maxToSpawn,
        int forcedBurstId,
        Vector3? originWorld,
        Vector3? repelFromWorld,
        float burstImpulse,
        float spreadAngleDeg,
        float spawnJitterRadius,
        InstrumentTrack.BurstPlacementMode placementMode,
        int trapSearchRadiusCells,
        int trapBufferCells,
        int forcedTargetBin)
    {
        string name = _getName();
        var controller = _getController();
        var drumTrack = _getDrumTrack();
        var collectablePrefab = _getCollectablePrefab();

        // --- Entry guards ---
        if (noteSet == null)
        {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=noteSet_null maxToSpawn={maxToSpawn}");
            return;
        }
        if (collectablePrefab == null)
        {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=collectablePrefab_null noteSet={noteSet} maxToSpawn={maxToSpawn}");
            return;
        }
        if (controller == null || controller.noteVisualizer == null)
        {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=controller_or_noteVisualizer_null controllerNull={(controller == null)} noteVizNull={(controller != null && controller.noteVisualizer == null)} noteSet={noteSet} maxToSpawn={maxToSpawn}");
            return;
        }

        // --- Burst ID: choose exactly once; never change it mid-function ---
        int burstId = forcedBurstId > 0 ? forcedBurstId : ++_nextBurstId;
        if (forcedBurstId > 0) _nextBurstId = Mathf.Max(_nextBurstId, forcedBurstId);
        _setCurrentBurstId(burstId);

        // Sweep sibling tracks for stale placeholders from their own prior bursts
        if (controller.tracks != null && controller.noteVisualizer != null)
        {
            foreach (var sibling in controller.tracks)
            {
                if (sibling == null) continue;
                controller.noteVisualizer.CanonicalizeTrackMarkers(sibling, sibling.currentBurstId);
            }
        }

        int loopMultiplier = _getLoopMultiplier();

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRKDBG] {name} SpawnCollectableBurst: burstId={_getCurrentBurstId()} noteSet={noteSet} " +
                  $"stepCount={(noteSet?.GetStepList()?.Count ?? -1)} noteCount={(noteSet?.GetNoteList()?.Count ?? -1)} " +
                  $"loopMul={loopMultiplier} pendingExpand={_isExpansionPending()} MaxSpawnCount: {maxToSpawn}");

        var stepList = noteSet.GetStepList();
        var noteList = noteSet.GetNoteList();

        if (stepList == null || stepList.Count == 0)
        {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=stepList_empty");
            return;
        }
        if (noteList == null || noteList.Count == 0)
        {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=noteList_empty");
            return;
        }

        _setCurrentBurstArmed(true);
        _setCurrentBurstRemaining(0);

        int binSize = _getBinSize();

        // --- Step normalization: deduplicate and clamp to bin-local space ---
        int rawCount = stepList.Count;
        bool hadOutOfRange = false;
        var localSteps = new List<int>(rawCount);
        var seenLocal = new HashSet<int>();

        for (int i = 0; i < rawCount; i++)
        {
            int raw = stepList[i];
            if (raw < 0) continue;
            if (raw >= binSize) hadOutOfRange = true;
            int local = raw % binSize;
            if (seenLocal.Add(local)) localSteps.Add(local);
        }

        if (hadOutOfRange || localSteps.Count != rawCount)
            Debug.LogWarning($"[TRK:STEP_NORMALIZE] track={name} burstId={burstId} binSize={binSize} " +
                             $"rawSteps={rawCount} localUnique={localSteps.Count} hadOutOfRange={hadOutOfRange} " +
                             $"sampleRaw={string.Join(",", stepList.GetRange(0, Mathf.Min(8, rawCount)))} " +
                             $"sampleLocal={string.Join(",", localSteps.GetRange(0, Mathf.Min(8, localSteps.Count)))}");

        // --- Target bin: forcedTargetBin > expansion override > controller selection ---
        var expansionCtrl = _getExpansionCtrl();
        int targetBin;
        if (forcedTargetBin >= 0)
        {
            targetBin = Mathf.Clamp(forcedTargetBin, 0, Mathf.Max(0, loopMultiplier - 1));
            if (targetBin != forcedTargetBin)
                Debug.LogWarning($"[TRK:BURST] forcedTargetBin={forcedTargetBin} clamped to {targetBin} (loopMul={loopMultiplier}) track={name} burstId={burstId}");
        }
        else if (expansionCtrl != null && expansionCtrl.OverrideNextSpawnBin >= 0)
        {
            targetBin = expansionCtrl.ConsumeOverrideNextSpawnBin();
        }
        else
        {
            targetBin = _resolveTargetBinFromController();
        }

        // --- Expansion staging: target bin exceeds committed loop width ---
        if (targetBin >= loopMultiplier)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[TRK:BURST] OUTCOME=STAGE_EXPAND track={name} burstId={burstId} " +
                $"targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");

            var stagedBurst = new TrackExpansionController.PendingBurstData
            {
                noteSet = noteSet, maxToSpawn = maxToSpawn, burstId = burstId,
                originWorld = originWorld, repelFromWorld = repelFromWorld,
                burstImpulse = burstImpulse, spreadAngleDeg = spreadAngleDeg,
                spawnJitterRadius = spawnJitterRadius, placementMode = placementMode,
                trapSearchRadiusCells = trapSearchRadiusCells, trapBufferCells = trapBufferCells,
                intendedTargetBin = targetBin,
            };

            Vector3 voidPos = originWorld ?? repelFromWorld ?? _getHostPosition();
            bool staged = expansionCtrl.TryStageExpand(stagedBurst, targetBin, voidPos);

            if (staged)
            {
                _beginGravityVoidForPendingExpandIfEligible(voidPos);

                foreach (var t in controller.tracks)
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[RECOMPUTE] Attempting to recompute track {t}");
                    if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
                }
                return;
            }

            // Stage rejected (expansion already pending) — fall through to density spawn
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BURST] STAGE_EXPAND rejected (already pending) → density spawn track={name} burstId={burstId}");
            targetBin = Mathf.Clamp(_getNextBinForSpawn(), 0, Mathf.Max(0, loopMultiplier - 1));
        }

        // --- Backfill empty bins before the target so they don't stay silent after ascension ---
        if (forcedTargetBin < 0)
            BackfillEmptyBins(noteSet, targetBin, maxToSpawn, originWorld, repelFromWorld,
                burstImpulse, spreadAngleDeg, spawnJitterRadius, placementMode,
                trapSearchRadiusCells, trapBufferCells);

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_NOW track={name} burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");

        // --- Step-sequenced spawning (the only spawn path) ---
        EnqueueCompositionSpawns(noteSet, localSteps, targetBin, binSize, maxToSpawn,
            originWorld, spawnJitterRadius, burstId);
    }

    // Spawns a collectable for each preceding empty bin so they don't stay silent after ascension.
    // Only called for top-level (non-forced) bursts to prevent infinite recursion.
    private void BackfillEmptyBins(
        NoteSet noteSet, int targetBin, int maxToSpawn,
        Vector3? originWorld, Vector3? repelFromWorld,
        float burstImpulse, float spreadAngleDeg, float spawnJitterRadius,
        InstrumentTrack.BurstPlacementMode placementMode, int trapSearchRadiusCells, int trapBufferCells)
    {
        int loopMultiplier = _getLoopMultiplier();
        for (int b = 0; b < targetBin; b++)
        {
            if (b < loopMultiplier && !_hasAnyNoteInBin(b))
            {
                SpawnBurst(noteSet, maxToSpawn, -1,
                    originWorld, repelFromWorld, burstImpulse, spreadAngleDeg, spawnJitterRadius,
                    placementMode, trapSearchRadiusCells, trapBufferCells,
                    forcedTargetBin: b);
            }
        }
    }

    /// <summary>
    /// Deterministic destination: absStep maps to the X column (0 = left edge, finalStep = right edge),
    /// pitch maps to the Y row across the NoteSet's note range (lowest = bottom, highest = top).
    /// Dust does not block — landing in dust is the intentional trapped-spawn case.
    /// </summary>
    private bool TryResolveSpawnDestinationCell(
        DrumTrack drumTrack,
        HashSet<Vector2Int> usedCellsThisBurst,
        int absStep,
        int finalStep,
        int note,
        NoteSet noteSet,
        out Vector2Int chosenCell)
    {
        chosenCell = default;

        int w = drumTrack != null ? drumTrack.GetSpawnGridWidth() : 0;
        int h = drumTrack != null ? drumTrack.GetSpawnGridHeight() : 0;
        if (w <= 0 || h <= 0) return false;

        float cellWorld = Mathf.Max(0.001f, drumTrack.GetCellWorldSize());
        Vector2 halfExtents = Vector2.one * (cellWorld * 0.45f);

        float xNorm = finalStep > 0 ? Mathf.Clamp01((float)absStep / finalStep) : 0f;
        int cx = Mathf.Clamp(Mathf.RoundToInt(xNorm * (w - 1)), 0, w - 1);

        float yNorm = 0.5f;
        if (noteSet != null && noteSet.TryGetNoteRange(out int minNote, out int maxNote) && maxNote > minNote)
            yNorm = Mathf.InverseLerp(minNote, maxNote, note);
        int cy = Mathf.Clamp(Mathf.RoundToInt(yNorm * (h - 1)), 0, h - 1);

        var spawnBlockedMask = _getSpawnBlockedMask();

        bool IsHardBlocked(Vector2Int gp)
        {
            if (usedCellsThisBurst != null && usedCellsThisBurst.Contains(gp)) return true;
            if (!Collectable.IsCellFreeStatic(gp)) return true;
            Vector2 wp = (Vector2)drumTrack.GridToWorldPosition(gp);
            return Physics2D.OverlapBox(wp, halfExtents * 2f, 0f, spawnBlockedMask) != null;
        }

        // Scan outward from the exact cell; every same-column candidate beats any
        // one-column-away candidate so repeated notes stack vertically before drifting sideways.
        for (int adx = 0; adx < w; adx++)
        {
            for (int sx = 0; sx < (adx == 0 ? 1 : 2); sx++)
            {
                int x = cx + (sx == 0 ? adx : -adx);
                if (x < 0 || x >= w) continue;
                for (int ady = 0; ady < h; ady++)
                {
                    for (int sy = 0; sy < (ady == 0 ? 1 : 2); sy++)
                    {
                        int y = cy + (sy == 0 ? ady : -ady);
                        if (y < 0 || y >= h) continue;
                        var gp = new Vector2Int(x, y);
                        if (IsHardBlocked(gp)) continue;
                        chosenCell = gp;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Composition Mode: step-sequenced burst helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pre-allocates grid cells for a burst and queues one PendingCompositionLaunch per step.
    /// Each collectable launches (with SFX) when the loop's step counter reaches its absStep.
    /// </summary>
    private void EnqueueCompositionSpawns(
        NoteSet noteSet,
        List<int> localSteps,
        int targetBin,
        int binSize,
        int maxToSpawn,
        Vector3? originWorld,
        float spawnJitterRadius,
        int burstId)
    {
        string name = _getName();
        var gfm = GameFlowManager.Instance;
        var dustGen = gfm?.dustGenerator;
        var drumTrack = _getDrumTrack();
        int gridW = drumTrack != null ? drumTrack.GetSpawnGridWidth() : 0;
        int gridH = drumTrack != null ? drumTrack.GetSpawnGridHeight() : 0;

        if (gridW <= 0 || gridH <= 0 || dustGen == null || drumTrack == null)
        {
            _setCurrentBurstArmed(false);
            _setCurrentBurstRemaining(0);
            Debug.LogWarning($"[TRK:COMP] ABORT track={name} burstId={burstId} reason=grid_or_dust_invalid");
            return;
        }

        var usedCells = new HashSet<Vector2Int>();
        var usedAbsSteps = new HashSet<int>();
        int queued = 0;

        foreach (int step in localSteps)
        {
            if (maxToSpawn > 0 && queued >= maxToSpawn) break;

            int absStep = targetBin * binSize + step;
            if (!usedAbsSteps.Add(absStep)) continue;

            int note = _getNoteForPhaseAndRole(noteSet, step);
            int dur;
            float vel127;
            if (!noteSet.TryGetTemplateTimingAtStep(step, out dur, out vel127))
            {
                dur = 480;
                vel127 = 100f;
            }

            int finalStep = (targetBin + 1) * binSize;

            Vector2Int cell;
            if (!TryResolveSpawnDestinationCell(drumTrack, usedCells, absStep, finalStep, note, noteSet, out cell)) continue;
            usedCells.Add(cell);

            bool hasDust = dustGen.HasDustAt(cell);
            if (hasDust)
            {
                const float jailHold = 4f;
                dustGen.CreateJailCenterForCollectable(cell, jailHold, ownerId: burstId);
            }

            Vector3 targetWorld = drumTrack.GridToWorldPosition(cell);
            if (originWorld.HasValue && spawnJitterRadius > 0f)
                targetWorld += (Vector3)(UnityEngine.Random.insideUnitCircle * spawnJitterRadius);

            _pendingCompositionLaunches.Add(new PendingCompositionLaunch
            {
                absStep = absStep,
                note = note,
                duration = dur,
                velocity127 = vel127,
                originWorld = originWorld ?? _getHostPosition(),
                targetWorld = targetWorld,
                targetCell = cell,
                cellHasDust = hasDust,
                noteSet = noteSet,
                burstId = burstId,
            });
            queued++;
        }

        if (queued <= 0)
        {
            _setCurrentBurstArmed(false);
            _setCurrentBurstRemaining(0);
            _raiseBurstCleared(burstId, false);
            Debug.LogWarning($"[TRK:COMP] EMPTY track={name} burstId={burstId}");
            return;
        }

        // Burst bookkeeping (mirrors existing SPAWN_NOW path).
        _registerBurstState(burstId, queued, targetBin);
        _setCurrentBurstRemaining(queued);

        _setBinAllocated(targetBin, true);
        _advanceCursorPastBin(targetBin);

        // Subscribe to per-step events (idempotent).
        if (!_compositionStepListenerActive && drumTrack != null)
        {
            drumTrack.OnStepChanged += OnCompositionStepFired;
            _compositionStepListenerActive = true;
        }

        // Spawn the origin effect at MineNode destruction time (now), not at first step launch.
        var compositionSpawnEffectPrefab = _getCompositionSpawnEffectPrefab();
        if (_compositionSpawnEffect == null && compositionSpawnEffectPrefab != null)
        {
            Vector3 effectPos = originWorld ?? _getHostPosition();
            _compositionSpawnEffect = UnityEngine.Object.Instantiate(compositionSpawnEffectPrefab, effectPos, Quaternion.identity);
            var main = _compositionSpawnEffect.main;
            main.startColor = new ParticleSystem.MinMaxGradient(_getDisplayColor());
            main.stopAction = ParticleSystemStopAction.Destroy;
            _compositionSpawnEffect.Play();
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:COMP] QUEUED track={name} burstId={burstId} count={queued} targetBin={targetBin}");
    }

    /// <summary>
    /// Fires when the drum transport advances a step. Launches any pending collectable
    /// whose absStep matches the current step index.
    /// </summary>
    private void OnCompositionStepFired(int step, int leaderSteps)
    {
        var controller = _getController();
        var drumTrack = _getDrumTrack();
        var collectablePrefab = _getCollectablePrefab();
        var collectableParent = _getCollectableParent();

        for (int i = _pendingCompositionLaunches.Count - 1; i >= 0; i--)
        {
            var launch = _pendingCompositionLaunches[i];
            if (launch.absStep != step) continue;

            _pendingCompositionLaunches.RemoveAt(i);

            var nv = controller?.noteVisualizer;

            // Instantiate collectable at the MineNode origin.
            var go = UnityEngine.Object.Instantiate(collectablePrefab, launch.originWorld, Quaternion.identity, collectableParent);
            if (!go) continue;
            if (!go.TryGetComponent(out Collectable c)) { UnityEngine.Object.Destroy(go); continue; }

            c.burstId = launch.burstId;
            c.intendedStep = launch.absStep;
            c.intendedBin = _getBurstTargetBinOrZero(launch.burstId);
            _assignInstrumentTrack(c);
            c.isTrappedInDust = launch.cellHasDust;
            c.spawnVelocity127 = launch.velocity127;

            _hookCollectableDestroyHandler(c);
            _placeAndBindPlaceholderMarker(nv, c, launch.absStep, launch.burstId);

            _applyTrackVisuals(c);

            // SFX: play the authored note as the collectable launches (one-time MIDI playthrough).
            _playOneShotMidi(launch.note, launch.velocity127, launch.duration);

            // Begin intro flight from origin to grid cell.
            _scratchSteps.Clear();
            _scratchSteps.Add(launch.absStep);
            _beginSpawnArrival(c, launch.originWorld, launch.targetWorld, launch.note, launch.duration, launch.noteSet, _scratchSteps);

            _spawnedCollectables.Add(go);
        }

        // Unsubscribe once all pending launches have fired.
        if (_pendingCompositionLaunches.Count == 0 && _compositionStepListenerActive)
        {
            if (drumTrack != null) drumTrack.OnStepChanged -= OnCompositionStepFired;
            _compositionStepListenerActive = false;
            _canonicalizeOwnMarkers();

            if (_compositionSpawnEffect != null)
            {
                _compositionSpawnEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                _compositionSpawnEffect = null;
            }
        }
    }

    /// <summary>
    /// Clears any queued launches and unsubscribes the step listener. Called when in-flight
    /// collectables are force-despawned (e.g. scene/track reset).
    /// </summary>
    public void ClearPendingLaunches()
    {
        _pendingCompositionLaunches.Clear();
        var drumTrack = _getDrumTrack();
        if (_compositionStepListenerActive && drumTrack != null)
        {
            drumTrack.OnStepChanged -= OnCompositionStepFired;
            _compositionStepListenerActive = false;
        }
        if (_compositionSpawnEffect != null)
        {
            _compositionSpawnEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            _compositionSpawnEffect = null;
        }
    }
}
