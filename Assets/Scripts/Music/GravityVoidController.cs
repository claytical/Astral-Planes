using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Owns the "Gravity Void" particle VFX + dust-ring imprint lifecycle that plays while a
// track's expansion is pending. Extracted from InstrumentTrackController, which still owns
// the SerializeField prefab/parent references and injects them (and transport/config lookups)
// via delegates, mirroring CosmicDustRegrowthController's host+delegate shape.
public sealed class GravityVoidController
{
    private readonly MonoBehaviour _host;
    private readonly Func<GameFlowManager> _getGfm;
    private readonly Func<GameObject> _getPrefab;
    private readonly Func<Transform> _getParent;
    private readonly Func<float> _getGravityVoidScale;
    private readonly Func<int> _getVoidRingWidthCells;
    private readonly Func<float> _getGravityVoidImprintTickSeconds;
    private readonly Func<int> _getPlayheadBin;
    private readonly Func<MusicalRole, int> _getFilledBinCountForRole;

    private Vector2Int _gravityVoidCenterGP;
    private bool _gravityVoidHasCenterGP;
    private readonly List<Vector2Int> _vehicleCellsScratch = new();
    private GameObject _gravityVoidInstance;
    private ParticleSystem[] _gravityVoidParticles;
    // Cache prefab alpha per particle system so alpha doesn't compound.
    private float[] _gravityVoidPrefabStartAlpha;
    // Current outer radius for VFX scaling.
    private int _gravityVoidCurrentOuterR;
    private Coroutine _gravityVoidRoutine;
    private InstrumentTrack _gravityVoidOwner;
    private Vector3 _gravityVoidCenterWorld;
    private Color _gravityVoidParticleTint;
    private Color _gravityVoidDustImprintTint;
    private int _gravityVoidMaxRadiusRuntime = -1;
    // Non-owner active roles ranked most-filled-bins first (innermost ring) down to
    // least-filled last (outer rim). Recomputed on every Begin call (see
    // BeginGravityVoidForPendingExpand); re-read per new-bin tick by the growth
    // routine rather than snapshotted once, so mid-flight fill changes affect
    // rings not yet imprinted.
    private IReadOnlyList<MusicalRole> _gravityVoidRankedRoles;
    // Last duration actually applied to the particle systems; avoids re-touching
    // main.duration/startLifetime (illegal while playing) when nothing has changed.
    private float _gravityVoidAppliedDurationSeconds = -1f;

    public GravityVoidController(
        MonoBehaviour host,
        Func<GameFlowManager> getGfm,
        Func<GameObject> getPrefab,
        Func<Transform> getParent,
        Func<float> getGravityVoidScale,
        Func<int> getVoidRingWidthCells,
        Func<float> getGravityVoidImprintTickSeconds,
        Func<int> getPlayheadBin,
        Func<MusicalRole, int> getFilledBinCountForRole)
    {
        _host = host;
        _getGfm = getGfm;
        _getPrefab = getPrefab;
        _getParent = getParent;
        _getGravityVoidScale = getGravityVoidScale;
        _getVoidRingWidthCells = getVoidRingWidthCells;
        _getGravityVoidImprintTickSeconds = getGravityVoidImprintTickSeconds;
        _getPlayheadBin = getPlayheadBin;
        _getFilledBinCountForRole = getFilledBinCountForRole;
    }

    // Ranks non-owner active roles by fill level, most-filled first (innermost ring) down to
    // least-filled last (outer rim). Ties keep original activeRoles order (stable sort).
    // Falls back to the full activeRoles list (including owner) if filtering empties the set
    // (e.g. a single-active-role motif where the owner is the only active role) so callers
    // never divide by zero / index an empty list.
    private IReadOnlyList<MusicalRole> BuildRankedRoles(IReadOnlyList<MusicalRole> activeRoles)
    {
        if (activeRoles == null || activeRoles.Count == 0) return activeRoles;

        var ownerRole = _gravityVoidOwner != null ? _gravityVoidOwner.assignedRole : MusicalRole.None;

        var candidates = new List<MusicalRole>(activeRoles.Count);
        foreach (var r in activeRoles)
            if (r != ownerRole) candidates.Add(r);

        if (candidates.Count == 0) return activeRoles;

        return candidates
            .OrderByDescending(r => _getFilledBinCountForRole?.Invoke(r) ?? 0)
            .ToList();
    }

    private float GetSecondsRemainingInCurrentBin()
    {
        var gfm = _getGfm();
        var drum = gfm?.activeDrumTrack;
        if (drum == null) return 0f;

        double start = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime;

        if (start <= 0.0) return 0f;

        float clipLen = drum.GetAuthoredBaseLoopSeconds();

        if (clipLen <= 0f) return 0f;

        double delta = AudioSettings.dspTime - start;

        if (delta < 0.0) return clipLen;

        double into = delta % clipLen;
        float rem = (float)(clipLen - into);

        return Mathf.Clamp(rem, 0f, clipLen);
    }

    public void BeginGravityVoidForPendingExpand(InstrumentTrack ownerTrack, Vector3 centerWorld, Vector2Int centerGP)
    {
        if (ownerTrack == null) return;

        // Is this a repeat Begin for the same owner while already active?
        bool sameOwner = (_gravityVoidOwner != null && ownerTrack == _gravityVoidOwner);
        bool routineRunning = (_gravityVoidRoutine != null);

        // Update owner + center every time (Begin acts as "refresh" too).
        _gravityVoidOwner = ownerTrack;
        _gravityVoidCenterWorld = centerWorld;
        _gravityVoidCenterGP = centerGP;
        _gravityVoidHasCenterGP = true;

        var c = (ownerTrack.ActiveProfile ?? MusicalRoleProfileLibrary.GetProfile(ownerTrack.assignedRole))?.GetBaseColor() ?? ownerTrack.DisplayColor;
        _gravityVoidDustImprintTint = c;
        _gravityVoidParticleTint    = new Color(c.r, c.g, c.b, 1f);

        // Spawn if needed, otherwise update visuals/position (must not destroy/recreate).
        SpawnOrUpdateGravityVoid(_gravityVoidCenterWorld, _gravityVoidParticleTint);

        // Recompute runtime parameters; these can change while pending. Runs unconditionally
        // (even on same-owner refresh calls that skip the coroutine restart below) so the
        // ranked-role list — and the max radius derived from its count — stay current with
        // live fill counts for as long as the void is pending.
        _gravityVoidMaxRadiusRuntime = -1;

        {
            var gfm = _getGfm();
            var pool = gfm?.activeDrumTrack?._starPool;
            var motifRoles = pool?.GetAnyActiveStarMotifRoles();
            IReadOnlyList<MusicalRole> activeRolesForRanking =
                (motifRoles != null && motifRoles.Count > 0)
                    ? motifRoles
                    : new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };

            _gravityVoidRankedRoles = BuildRankedRoles(activeRolesForRanking);

            int m = _gravityVoidRankedRoles.Count;
            int ringsPerSubseqCalc = Mathf.Max(1, m / 2);
            int totalRings = m + (Mathf.Max(1, _gravityVoidOwner.loopMultiplier) - 1) * ringsPerSubseqCalc;
            _gravityVoidMaxRadiusRuntime = totalRings * _getVoidRingWidthCells();
        }

        // --- CRITICAL: don't restart the coroutine if it's already running for this owner. ---
        // Restarting here is what looks like "respawn at the boundary".
        if (sameOwner && routineRunning)
        {
            return;
        }

        // If owner changed, or routine is missing, (re)start cleanly.
        if (_gravityVoidRoutine != null)
        {
            _host.StopCoroutine(_gravityVoidRoutine);
            _gravityVoidRoutine = null;
        }

        _gravityVoidRoutine = _host.StartCoroutine(GravityVoidGrowAndImprintRoutine());
    }

    public void EndGravityVoidForPendingExpand(InstrumentTrack ownerTrack)
    {
        // If caller provides an owner, only allow the owner to end it.
        if (ownerTrack != null && _gravityVoidOwner != null && ownerTrack != _gravityVoidOwner)
        {
            return;
        }
        _gravityVoidOwner = null;
        _gravityVoidHasCenterGP = false;

        if (_gravityVoidRoutine != null)
        {
            _host.StopCoroutine(_gravityVoidRoutine);
            _gravityVoidRoutine = null;
        }
        if (_gravityVoidInstance == null) return;
        var explode = _gravityVoidInstance.GetComponent<Explode>();
        if (explode != null) explode.Permanent();

        DespawnGravityVoid();
    }

    private IEnumerator GravityVoidGrowAndImprintRoutine()
    {
        var gfm = _getGfm();
        if (gfm?.dustGenerator == null) yield break;

        int W               = _getVoidRingWidthCells();
        int completedRings  = 0;
        int lastBurstBin    = -1;
        float tick          = Mathf.Max(0.01f, _getGravityVoidImprintTickSeconds());

        while (_gravityVoidOwner != null)
        {
            SpawnOrUpdateGravityVoid(_gravityVoidCenterWorld, _gravityVoidParticleTint);

            int playheadBin = _getPlayheadBin();
            if (playheadBin != lastBurstBin && _gravityVoidHasCenterGP)
            {
                lastBurstBin = playheadBin;

                // Re-read the controller-cached ranked list here (not snapshotted at
                // coroutine start) so a same-owner Begin refresh mid-flight can affect
                // rings not yet imprinted. Already-imprinted rings are never recolored.
                var rankedRoles = _gravityVoidRankedRoles;
                if (rankedRoles == null || rankedRoles.Count == 0)
                    rankedRoles = new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };
                int m = rankedRoles.Count;
                int ringsPerSubseq = Mathf.Max(1, m / 2);

                int ringsThisBin = (playheadBin == 0) ? m : ringsPerSubseq;
                ringsThisBin = ApplyDensityThrottle(ringsThisBin);
                float growIn = Mathf.Max(0.05f, GetSecondsRemainingInCurrentBin());

                CollectVehicleGridCells();
                ImprintVoidRings(rankedRoles, m, W, ringsThisBin, completedRings, growIn);

                completedRings           += ringsThisBin;
                _gravityVoidCurrentOuterR = completedRings * W;
            }

            yield return new WaitForSeconds(tick);
        }

        _gravityVoidRoutine = null;
    }

    // Nothing thins dust back down within a motif — only the full ClearMaze() reset at the next
    // motif/phase transition does. As staged expansions repeatedly fire ring bursts, scale the
    // ring count down once the grid is already dense so clutter can't climb unbounded within a
    // single motif's void-growth window. Thresholds live on CosmicDustGeneratorConfig, shared
    // with CosmicDustGenerator's regrow-delay easing (ApplyDensityRegrowEasing) so both density
    // responses kick in at the same solid-fraction points.
    private int ApplyDensityThrottle(int ringsThisBin)
    {
        if (ringsThisBin <= 0) return ringsThisBin;

        var dustGen = _getGfm()?.dustGenerator;
        if (dustGen == null || dustGen.config == null) return ringsThisBin;

        float solidFraction = dustGen.GetSolidFraction();
        if (solidFraction >= dustGen.config.densityThrottleSkipAt) return 0;
        if (solidFraction >= dustGen.config.densityThrottleHalfAt) return Mathf.Max(1, ringsThisBin / 2);
        return ringsThisBin;
    }

    private void CollectVehicleGridCells()
    {
        _vehicleCellsScratch.Clear();
        var gfm = _getGfm();
        var vehicleList = gfm?.GetVehicles();
        if (vehicleList == null || gfm?.activeDrumTrack == null) return;
        foreach (var v in vehicleList)
        {
            if (v != null && v.isActiveAndEnabled)
                _vehicleCellsScratch.Add(gfm.activeDrumTrack.WorldToGridPosition(v.transform.position));
        }
    }

    private void ImprintVoidRings(
        IReadOnlyList<MusicalRole> activeRoles, int N, int W,
        int ringsThisBin, int completedRingsBefore, float growIn)
    {
        var dustGen = _getGfm()?.dustGenerator;
        if (dustGen == null) return;

        for (int r = 0; r < ringsThisBin; r++)
        {
            int globalRingIdx = completedRingsBefore + r;
            int innerR        = globalRingIdx * W;
            int outerR        = innerR + W;
            var ringRole      = activeRoles[globalRingIdx % N];
            var motif         = _getGfm()?.phaseTransitionManager?.currentMotif;
            var roleProfile   = motif?.GetConfigForRoleAtBin(ringRole, 0, 1, 0)?.roleProfile
                ?? MusicalRoleProfileLibrary.GetProfile(ringRole);

            dustGen.GrowVoidDustDiskFromGrid(
                centerGP:                  _gravityVoidCenterGP,
                outerRadiusCells:          outerR,
                imprintRole:               ringRole,
                hueRgb:                    roleProfile?.GetBaseColor() ?? _gravityVoidDustImprintTint,
                energyAtCenter01:          1f,
                falloffExp:                0.3f,
                growInSeconds:             growIn,
                fillWedges01To4:           4,
                vehicleCells:              _vehicleCellsScratch,
                vehicleNoSpawnRadiusCells: 1,
                maxCellsThisCall:          -1,
                innerRadiusCellsExclusive: innerR
            );
        }
    }

    private void DespawnGravityVoid()
    {
        if (_gravityVoidInstance == null)
            return;
        UnityEngine.Object.Destroy(_gravityVoidInstance);

        _gravityVoidInstance = null;
        _gravityVoidParticles = null;

        // Clear cached prefab alpha so the next spawn re-captures it
        _gravityVoidPrefabStartAlpha = null;

        // Reset VFX growth state
        _gravityVoidCurrentOuterR = 0;
        _gravityVoidAppliedDurationSeconds = -1f;

        // Clear center bookkeeping (defensive; owner is cleared elsewhere)
        _gravityVoidHasCenterGP = false;
    }

    private void SpawnOrUpdateGravityVoid(Vector3 worldPos, Color tint)
    {
        var prefab = _getPrefab();
        if (prefab == null) return;

        if (_gravityVoidInstance == null)
        {
            var configuredParent = _getParent();
            var parent = (configuredParent != null) ? configuredParent : _host.transform;
            _gravityVoidInstance = UnityEngine.Object.Instantiate(prefab, worldPos, Quaternion.identity, parent);

            float gravityVoidScale = _getGravityVoidScale();
            if (gravityVoidScale != 1f)
                _gravityVoidInstance.transform.localScale *= gravityVoidScale;

            _gravityVoidParticles = _gravityVoidInstance.GetComponentsInChildren<ParticleSystem>(true);

            // Cache each particle system's prefab alpha ONCE so we can preserve it.
            if (_gravityVoidParticles != null && _gravityVoidParticles.Length > 0)
            {
                _gravityVoidPrefabStartAlpha = new float[_gravityVoidParticles.Length];
                for (int i = 0; i < _gravityVoidParticles.Length; i++)
                {
                    var ps = _gravityVoidParticles[i];
                    if (ps == null) { _gravityVoidPrefabStartAlpha[i] = 1f; continue; }

                    var main = ps.main;

                    // startColor can be a gradient; this grabs a representative color.
                    // If your prefab uses gradients heavily and you need exactness, we can upgrade this,
                    // but for your use (white low-alpha ring), this is correct.
                    _gravityVoidPrefabStartAlpha[i] = main.startColor.color.a;
                }
            }
        }
        else
        {
            _gravityVoidInstance.transform.position = worldPos;
        }

        if (_gravityVoidParticles == null) return;

        // ----- OPTIONAL: scale VFX outward to match current radius -----
        // This assumes your prefab ring looks correct at "max" scale = config.gravityVoidScale.
        // We scale from a small minimum up to full based on current outer radius.
        if (_gravityVoidInstance != null && _gravityVoidMaxRadiusRuntime > 0)
        {
            float frac = Mathf.Clamp01((float)_gravityVoidCurrentOuterR / (float)_gravityVoidMaxRadiusRuntime);

            // Keep a visible presence even at the start (so it doesn't look like "nothing happens").
            const float minFrac = 0.15f;
            float s = Mathf.Lerp(minFrac, 1f, frac);

            // Preserve your authored prefab scale + config.gravityVoidScale multiplier.
            // We apply a uniform multiplier on top.
            Vector3 baseScale = Vector3.one * _getGravityVoidScale();
            _gravityVoidInstance.transform.localScale = baseScale * s;
        }

        // ----- Keep particle-system duration/lifetime roughly matched to how long the
        // pending burst is expected to take, so the ring's own animation cycle doesn't
        // visibly finish and idle while it waits for the event-driven despawn. -----
        float estimatedSeconds = EstimateGravityVoidDurationSeconds();

        // Unity forbids changing main.duration/startLifetime while a particle system is
        // playing, so only touch them (via a brief stop/restart) when the estimate has
        // actually moved; otherwise leave the already-applied duration alone.
        bool applyDuration = estimatedSeconds > 0f &&
            Mathf.Abs(estimatedSeconds - _gravityVoidAppliedDurationSeconds) > 0.01f;

        // ----- Tint particles without compounding alpha -----
        for (int i = 0; i < _gravityVoidParticles.Length; i++)
        {
            var ps = _gravityVoidParticles[i];
            if (ps == null) continue;

            var main = ps.main;

            float prefabA = 1f;
            if (_gravityVoidPrefabStartAlpha != null && i >= 0 && i < _gravityVoidPrefabStartAlpha.Length)
                prefabA = _gravityVoidPrefabStartAlpha[i];

            Color outC = tint;
            outC.a = prefabA * tint.a;      // preserve prefab alpha; tint.a is your multiplier

            main.startColor = outC;

            if (applyDuration)
            {
                bool wasPlaying = ps.isPlaying;
                if (wasPlaying)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                main.duration = estimatedSeconds;
                if (main.startLifetime.mode == ParticleSystemCurveMode.Constant)
                    main.startLifetime = estimatedSeconds;

                if (wasPlaying)
                    ps.Play(true);
            }
        }

        if (applyDuration)
            _gravityVoidAppliedDurationSeconds = estimatedSeconds;
    }

    // Estimates seconds until the pending/in-flight burst's last note should eject, purely
    // as a cosmetic hint for the void particle system's duration. Actual despawn timing is
    // event-driven (see InstrumentTrackCompositionSpawner.OnCompositionStepFired), so an
    // imprecise estimate here only affects how the ring's animation cycles, not correctness.
    private float EstimateGravityVoidDurationSeconds()
    {
        if (_gravityVoidOwner == null) return 0f;
        var gfm = _getGfm();
        var drum = gfm?.activeDrumTrack;
        if (drum == null) return 0f;

        int binSize = _gravityVoidOwner.BinSize();
        if (binSize <= 0) return 0f;

        int resolvedBin = _gravityVoidOwner.PendingIntendedTargetBin ?? Mathf.Max(0, _gravityVoidOwner.loopMultiplier);
        int finalStep = (resolvedBin + 1) * binSize;

        int leaderSteps = Mathf.Max(1, drum.GetLeaderSteps());
        float stepDurationSec = drum.EffectiveLoopLengthSec / leaderSteps;

        return Mathf.Max(0f, finalStep * stepDurationSec);
    }
}
