using System;
using System.Collections;
using System.Collections.Generic;
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

    public GravityVoidController(
        MonoBehaviour host,
        Func<GameFlowManager> getGfm,
        Func<GameObject> getPrefab,
        Func<Transform> getParent,
        Func<float> getGravityVoidScale,
        Func<int> getVoidRingWidthCells,
        Func<float> getGravityVoidImprintTickSeconds,
        Func<int> getPlayheadBin)
    {
        _host = host;
        _getGfm = getGfm;
        _getPrefab = getPrefab;
        _getParent = getParent;
        _getGravityVoidScale = getGravityVoidScale;
        _getVoidRingWidthCells = getVoidRingWidthCells;
        _getGravityVoidImprintTickSeconds = getGravityVoidImprintTickSeconds;
        _getPlayheadBin = getPlayheadBin;
    }

    private float GetSecondsRemainingInCurrentBin()
    {
        var gfm = _getGfm();
        var drum = gfm?.activeDrumTrack;
        if (drum == null) return 0f;

        double start = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime;

        if (start <= 0.0) return 0f;

        float clipLen = drum.GetClipLengthInSeconds();

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

        var c = MusicalRoleProfileLibrary.GetProfile(ownerTrack.assignedRole)?.GetBaseColor() ?? ownerTrack.DisplayColor;
        _gravityVoidDustImprintTint = c;
        _gravityVoidParticleTint    = new Color(c.r, c.g, c.b, 1f);

        // Spawn if needed, otherwise update visuals/position (must not destroy/recreate).
        SpawnOrUpdateGravityVoid(_gravityVoidCenterWorld, _gravityVoidParticleTint);

        // Recompute runtime parameters; these can change while pending.
        _gravityVoidMaxRadiusRuntime = -1;

        if (_gravityVoidOwner != null)
        {
            // Radius mapping: 1 bin = 1 radius cell (visualize incoming bin => current + 1).
            int targetRadius = Mathf.Max(1, _gravityVoidOwner.loopMultiplier + 1);
            _gravityVoidMaxRadiusRuntime = targetRadius;
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

        // Safety bubble: activate at the MineNode capture position while the void is expanding.
        {
            var gfm = _getGfm();
            var pool = gfm?.activeDrumTrack?._starPool;

            // Compute max outer radius from ring formula so VFX sizes correctly.
            var motifRoles = pool?.GetAnyActiveStarMotifRoles();
            int roleCount = (motifRoles != null && motifRoles.Count > 0) ? motifRoles.Count : 4;
            int ringsPerSubseq = Mathf.Max(1, roleCount / 2);
            int totalRings = roleCount + (Mathf.Max(1, _gravityVoidOwner.loopMultiplier) - 1) * ringsPerSubseq;
            _gravityVoidMaxRadiusRuntime = totalRings * _getVoidRingWidthCells();
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

        var pool = gfm?.activeDrumTrack?._starPool;
        IReadOnlyList<MusicalRole> activeRoles = pool?.GetAnyActiveStarMotifRoles();
        if (activeRoles == null || activeRoles.Count == 0)
            activeRoles = new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };

        int N            = activeRoles.Count;
        int W            = _getVoidRingWidthCells();
        int ringsPerSubseq = Mathf.Max(1, N / 2);
        int completedRings = 0;
        int lastBurstBin   = -1;
        float tick         = Mathf.Max(0.01f, _getGravityVoidImprintTickSeconds());

        while (_gravityVoidOwner != null)
        {
            SpawnOrUpdateGravityVoid(_gravityVoidCenterWorld, _gravityVoidParticleTint);

            int playheadBin = _getPlayheadBin();
            if (playheadBin != lastBurstBin && _gravityVoidHasCenterGP)
            {
                lastBurstBin   = playheadBin;
                int ringsThisBin = (playheadBin == 0) ? N : ringsPerSubseq;
                float growIn     = Mathf.Max(0.05f, GetSecondsRemainingInCurrentBin());

                CollectVehicleGridCells();
                ImprintVoidRings(activeRoles, N, W, ringsThisBin, completedRings, growIn);

                completedRings           += ringsThisBin;
                _gravityVoidCurrentOuterR = completedRings * W;
            }

            yield return new WaitForSeconds(tick);
        }

        _gravityVoidRoutine = null;
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
            var roleProfile   = MusicalRoleProfileLibrary.GetProfile(ringRole);

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

            if (estimatedSeconds > 0f)
            {
                main.duration = estimatedSeconds;
                if (main.startLifetime.mode == ParticleSystemCurveMode.Constant)
                    main.startLifetime = estimatedSeconds;
            }
        }
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
