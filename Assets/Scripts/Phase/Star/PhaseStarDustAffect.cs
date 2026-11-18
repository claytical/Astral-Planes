using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
public sealed partial class PhaseStarDustAffect : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Material tentacleMaterial;

    [Tooltip("Shared PhaseStar tuning (Resources/Config/PhaseStarConfig). Unassigned = that asset is loaded.")]
    [SerializeField] private PhaseStarConfig config;
    private PhaseStarConfig _cfg;
    private PhaseStarConfig Cfg => _cfg ??= PhaseStarConfig.OrDefault(config, this);
    // Shared tuning (PhaseStarConfig → 5. Tentacles)
    private float minContactTime => Cfg.minContactTime;
    private float colorTransitionTime => Cfg.colorTransitionTime;
    private float drainBuildupSeconds => Cfg.drainBuildupSeconds;
    private float siphonTravelSeconds => Cfg.siphonTravelSeconds;
    private float siphonBrightness => Cfg.siphonBrightness;
    private float tentacleGrowSpeed => Cfg.tentacleGrowSpeed;
    private float tentacleRetractSpeed => Cfg.tentacleRetractSpeed;
    private float tentacleFlowSpeed => Cfg.tentacleFlowSpeed;
    private float tentacleWidth => Cfg.tentacleWidth;
    private float dissolveDuration => Cfg.dissolveDuration;
    private int fallbackTentaclesPerRole => Cfg.fallbackTentaclesPerRole;
    private int invalidTargetRetryFrames => Cfg.invalidTargetRetryFrames;
    private float invalidTargetRetryMs => Cfg.invalidTargetRetryMs;
    private float growingRootAlpha => Cfg.growingRootAlpha;
    private float growingTipAlpha => Cfg.growingTipAlpha;
    private float drainingBaseAlpha => Cfg.drainingBaseAlpha;
    private float boreSeconds => Cfg.boreSeconds;
    private float boreFadeSeconds => Cfg.boreFadeSeconds;
    private float boreTimeout => Cfg.boreTimeout;
    private float vineFollowSharpness => Cfg.vineFollowSharpness;
    private float vineMeander => Cfg.vineMeander;
    private float vineMeanderMaxCells => Cfg.vineMeanderMaxCells;
    private float vineMeanderSpacingCells => Cfg.vineMeanderSpacingCells;
    private float vineRootSlackCells => Cfg.vineRootSlackCells;
    private float noiseAmp => Cfg.noiseAmp;
    private float noiseSpeed => Cfg.noiseSpeed;
    private float weaveAmplitude => Cfg.weaveAmplitude;
    private float weaveWavelength => Cfg.weaveWavelength;
    private float weaveSpeed => Cfg.weaveSpeed;
    private bool shimmerEnabled => Cfg.shimmerEnabled;
    private float shimmerRatePerUnit => Cfg.shimmerRatePerUnit;
    private float shimmerLifetime => Cfg.shimmerLifetime;
    private float shimmerSize => Cfg.shimmerSize;
    private float shimmerAlpha => Cfg.shimmerAlpha;
    private float interTentacleStartInterval => Cfg.interTentacleStartInterval;

    private const int   SplinePoints      = 32;
    private const float KeepClearTick     = 0.15f;
    private const float DrainFlashDuration = 0.15f;
    private const float DiamondTipOffset  = 0.3f;

    private enum TentacleState
    {
        Idle,
        Growing,
        Draining,
        Retracting,
        Dissolving
    }

    private sealed class Tentacle
    {
        public MusicalRole role;
        public int tipIndex;
        public TentacleState state = TentacleState.Idle;
        public Vector2Int targetCell;
        public Vector2 targetWorldPos;
        public Vector2 tipPos;
        public LineRenderer line;
        public ParticleSystem shimmerPS;
        public float flowOffset;
        public Vector3[] linePts;
        public Gradient gradient;

        // Last inputs BuildGrowingGradient rebuilt its keys from — lets it skip the
        // SetKeys allocation on frames where nothing that gradient depends on changed.
        public Color lastGrowingGradientRoleColor = new Color(-1f, -1f, -1f, -1f);
        public float lastGrowingGradientAlphaScale = float.NaN;

        public float growProgress;
        public float contactTimer;
        public float dissolveTimer;
        public float alphaScale = 1f;
        public float drainFlashTimer;
        public bool notifiedDrainLock;
        public int lineIndexInRole;
        public int invalidTargetFrames;
        public float invalidTargetMs;
        public bool clearStarted;
        public float clearTimer;
        public float clearDuration;
        public bool buildupStarted;
        public float effectiveBuildupSeconds; // drainBuildupSeconds × cell HP, set at buildup start
        public bool siphonActive;
        public float siphonT; // 1 = at tip, 0 = arrived at star root

        // Vine routing (PhaseStarDustAffect.Vine.cs)
        public readonly TentacleVineRoute route = new TentacleVineRoute();
        public float growUnitsPerSec;
        public bool boring;
        public Vector2Int boreCell;
        public float boreTimer;
        public Vector2 lastRootPos;
        public int rootCrumbs;          // breadcrumb corners at nodes[1..rootCrumbs], added by star movement
        public int lineCount;           // valid entries in linePts (variable: pinned to the route's corners)
        public Vector3[] rootRenderPts; // spring-smoothed star-end segment
        public bool rootRenderInit;
    }

    public Action<MusicalRole, float> onDelivery;
    public event Action<MusicalRole> OnAttuned;
    public event Action OnAllTentaclesRetracted;

    private GameFlowManager _gfm;
    private PhaseStarBehaviorProfile _profile;
    private PhaseStar _star;
    private PhaseStarCravingNavigator _navigator;
    private MusicalRole _attunedRole = MusicalRole.None;
    private readonly TentacleVinePlanner _vinePlanner = new TentacleVinePlanner();

    private bool _tentaclesActive;
    private bool _acquisitionEnabled = true;
    private bool _isRetractAllInProgress;
    private readonly List<Tentacle> _tentacles = new();
    private float _lastTentacleStartTime = 0f;

    private float _keepClearTimer;
    private readonly Dictionary<Vector2Int, Tentacle> _reservedCells = new();
    private readonly HashSet<Vector2Int> _zappedThisCycle = new();

    // Perf-only throttle on AssignIdleTentacleTargets' "is anything idle and worth
    // scanning for" check — distinct from interTentacleStartInterval (a player-visible
    // pacing knob). Tentacle growth itself ramps over real time, so a ~0.06s delay here
    // before a target is even considered is not perceptible.
    private const float kAcquisitionScanCooldown = 0.06f;
    private float _nextAcquisitionScanAt;

    // Reused across AssignIdleTentacleTargets calls instead of allocating fresh
    // collections every frame while any tentacle needs a target.
    private readonly List<Tentacle> _idleTentaclesScratch = new();
    private readonly List<Vector2Int> _seededTargetsScratch = new();
    private readonly HashSet<Vector2Int> _excludedCellsScratch = new();

    // Maze-aware navigation (StarNavigationParams on the motif's MazePatternConfig).
    private StarNavigationParams _nav = StarNavigationParams.Default;
    private PhaseStarNavMode _navMode = PhaseStarNavMode.Direct;
    private float _burrowTimer;
    private Rigidbody2D _rb;
    private readonly HashSet<Vector2Int> _burrowCellsScratch = new();

    public void Initialize(
        PhaseStarBehaviorProfile profile,
        PhaseStar star,
        StarNavigationParams nav = null,
        PhaseStarNavMode navMode = PhaseStarNavMode.Direct)
    {
        _nav = nav ?? StarNavigationParams.Default;
        _navMode = navMode;
        _burrowTimer = 0f;
        _rb = GetComponent<Rigidbody2D>();

        _attunedRole = MusicalRole.None;
        _profile = profile;
        _star = star;
        _navigator = GetComponent<PhaseStarCravingNavigator>();

        RebuildTentaclesForRole(_attunedRole);

        star.OnDisarmed += _ => SetTentaclesActive(false);
    }

    public void SetAttunedRole(MusicalRole role)
    {
        _attunedRole = role;
        RebuildTentaclesForRole(role);
        ResetTentacles();
    }

    public void SetTentaclesActive(bool active)
    {
        _tentaclesActive = active;
        SetAcquisitionEnabled(active, "set-tentacles-active");

        if (!active)
            ResetTentacles();
    }

    public void SetAcquisitionEnabled(bool enabled, string reason)
    {
        _acquisitionEnabled = enabled;
        _navigator?.SetHuntingEnabled(enabled && _tentaclesActive);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStarDust] acquisition enabled={_acquisitionEnabled} tentaclesActive={_tentaclesActive} reason={reason}");
    }

    public void BeginRetractionForActiveTentacles()
    {
        SetAcquisitionEnabled(false, "begin-retract-all");
        _isRetractAllInProgress = true;

        Vector2 starPos = transform.position;
        foreach (var tentacle in _tentacles)
        {
            if (tentacle.state == TentacleState.Idle) continue;
            BeginRetractingTentacle(tentacle, starPos, "phase-star-waiting-for-retract");
        }

        TryNotifyAllTentaclesRetracted();
    }

    public void ResetTentacles()
    {
        Vector2 starPos = transform.position;

        foreach (var tentacle in _tentacles)
            ResetTentacleState(tentacle, starPos, destroyVisual: false);

        _lastTentacleStartTime = 0f;
        TryNotifyAllTentaclesRetracted();
    }

    public bool HasActiveTentacles
    {
        get
        {
            foreach (var tentacle in _tentacles)
            {
                if (tentacle.state == TentacleState.Growing  ||
                    tentacle.state == TentacleState.Draining ||
                    tentacle.state == TentacleState.Retracting ||
                    tentacle.state == TentacleState.Dissolving)
                    return true;
            }
            return false;
        }
    }

    public void GetDiamondLockState(int diamondIndex, out bool locked, out float lockAngleDeg, out bool isDraining)
    {
        locked       = false;
        lockAngleDeg = 0f;
        isDraining   = false;

        Tentacle best    = null;
        int      bestPri = -1;

        foreach (var tentacle in _tentacles)
        {
            int diamondForTentacle = (tentacle.tipIndex == 0 || tentacle.tipIndex == 2) ? 0 : 1;
            if (diamondForTentacle != diamondIndex) continue;

            int priority = tentacle.state == TentacleState.Draining ? 2 :
                           tentacle.state == TentacleState.Growing   ? 1 : -1;
            if (priority < 0) continue;

            if (priority > bestPri)
            {
                bestPri = priority;
                best    = tentacle;
            }
        }

        if (best == null) return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) return;

        // Aim where the vine actually leaves the star (it may route around dust), not straight at the target.
        Vector2 starPos  = transform.position;
        Vector2 targetPt = best.route.Count >= 2
            ? best.route.Sample(Mathf.Min(best.route.TotalLength, Mathf.Max(0.01f, drum.GetCellWorldSize())))
            : best.targetWorldPos;
        Vector2 dir      = targetPt - starPos;
        dir = dir.sqrMagnitude > 0.0001f
            ? dir.normalized * (dir.magnitude + DiamondTipOffset)
            : Vector2.up * DiamondTipOffset;

        float baseAngle = Mathf.Atan2(-dir.x, dir.y) * Mathf.Rad2Deg;
        lockAngleDeg = (best.tipIndex >= 2) ? baseAngle + 180f : baseAngle;
        locked     = true;
        isDraining = best.state == TentacleState.Draining;
    }

    private void Update()
    {
        // Burrow runs whenever the star moves (drifting or hunting), not just while tentacles are out.
        if (_navMode == PhaseStarNavMode.Burrow)
        {
            _burrowTimer += Time.deltaTime;
            if (_burrowTimer >= _nav.burrowCarveInterval)
            {
                _burrowTimer = 0f;
                TickBurrow();
            }
        }

        if (!_tentaclesActive)
            return;

        float dt = Time.deltaTime;
        _zappedThisCycle.Clear();

        _keepClearTimer += dt;
        if (_keepClearTimer >= KeepClearTick)
        {
            _keepClearTimer = 0f;
            TickKeepClear();
        }

        EnsureTentaclePoolForRole();
        AssignIdleTentacleTargets();

        foreach (var tentacle in _tentacles)
            TickTentacle(tentacle, dt);
    }

    private void EnsureTentaclePoolForRole()
    {
        if (_attunedRole == MusicalRole.None) return;

        int desiredCount = DetermineDesiredTentacleCount();
        int currentCount = 0;
        for (int i = 0; i < _tentacles.Count; i++)
            if (_tentacles[i].role == _attunedRole) currentCount++;

        if (currentCount == desiredCount) return;

        Vector2 starPos = transform.position;

        while (currentCount < desiredCount)
        {
            int roleIndex = currentCount;
            var tentacle = new Tentacle
            {
                role = _attunedRole,
                tipIndex = roleIndex % 4,
                lineIndexInRole = roleIndex,
                flowOffset = UnityEngine.Random.value,
                tipPos = transform.position
            };
            AllocateTentacleBuffers(tentacle);
            tentacle.line = CreateTentacleLine(_attunedRole, tentacle);
            _tentacles.Add(tentacle);
            currentCount++;
        }

        for (int i = _tentacles.Count - 1; i >= 0 && currentCount > desiredCount; i--)
        {
            var t = _tentacles[i];
            if (t.role != _attunedRole || t.state != TentacleState.Idle) continue;
            ResetTentacleState(t, starPos, destroyVisual: true);
            _tentacles.RemoveAt(i);
            currentCount--;
        }
    }

    private void TickKeepClear()
    {
        if (_profile == null || !_profile.starKeepsDustClear)
            return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen  = _gfm?.dustGenerator;
        var drum = _gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        Vector2Int center = drum.WorldToGridPosition(transform.position);
        int radius = Mathf.Max(0, _profile.starKeepClearRadiusCells + _nav.keepClearRadiusOffset);
        gen.SetStarKeepClear(center, radius, forceRemoveExisting: false);
    }

    // Burrow nav mode: carve a small disk one cell ahead of the star's heading so it can tunnel
    // through mazes with no corridors (FullFill). Regrows normally via DustClearSource.StarBurrow.
    // Leaves the attuned role's cells and any tentacle-reserved cells alone — those are the
    // star's own zap targets and carving them would throw away their energy.
    private void TickBurrow()
    {
        if (_rb == null) return;
        Vector2 vel = _rb.linearVelocity;
        if (vel.sqrMagnitude < 0.01f) return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen  = _gfm?.dustGenerator;
        var drum = _gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int starCell = drum.WorldToGridPosition(transform.position);
        Vector2 dir = vel.normalized;
        var ahead = new Vector2Int(
            starCell.x + Mathf.RoundToInt(dir.x),
            starCell.y + Mathf.RoundToInt(dir.y));

        _burrowCellsScratch.Clear();
        DustGridMath.FillDisk(_burrowCellsScratch, ahead, _nav.burrowCarveRadiusCells, w, h);

        foreach (var cell in _burrowCellsScratch)
        {
            if (_reservedCells.ContainsKey(cell)) continue;
            if (!gen.HasDustAt(cell)) continue;
            if (_attunedRole != MusicalRole.None &&
                gen.TryGetDustAt(cell, out var dust) && dust != null && dust.Role == _attunedRole)
                continue;

            gen.CarveCell(cell, fadeSeconds: 0.2f, scheduleRegrow: true,
                source: DustClearSource.StarBurrow, runPreExplode: false);
        }
    }

    private void AllocateTentacleBuffers(Tentacle tentacle)
    {
        tentacle.linePts   = new Vector3[SplinePoints];
        tentacle.rootRenderPts = new Vector3[kVineSubdivisions + 1];
        tentacle.gradient  = new Gradient();
        tentacle.alphaScale = 1f;
        tentacle.notifiedDrainLock = false;
        tentacle.clearStarted = false;
        tentacle.clearTimer = 0f;
        tentacle.clearDuration = 0f;
    }

    private void RebuildTentaclesForRole(MusicalRole role)
    {
        Vector2 starPos = transform.position;
        foreach (var t in _tentacles)
            ResetTentacleState(t, starPos, destroyVisual: true);
        _tentacles.Clear();

        if (role == MusicalRole.None) return;

        int tentacleCount = DetermineDesiredTentacleCount();
        for (int i = 0; i < tentacleCount; i++)
        {
            var tentacle = new Tentacle
            {
                role = role,
                tipIndex = i % 4,
                lineIndexInRole = i,
                flowOffset = UnityEngine.Random.value,
                tipPos = transform.position
            };
            AllocateTentacleBuffers(tentacle);
            tentacle.line = CreateTentacleLine(role, tentacle);
            _tentacles.Add(tentacle);
        }
    }

    private int DetermineDesiredTentacleCount()
    {
        // Pool = the node payload: one tentacle per note the next DiscoveryTrackNode will carry.
        // Concurrency below the cap is governed by AssignIdleTentacleTargets' zap budget.
        return Mathf.Max(1, _star != null ? _star.GetDesiredTentacleCount() : fallbackTentaclesPerRole);
    }

    private LineRenderer CreateTentacleLine(MusicalRole role, Tentacle tentacle)
    {
        var go = new GameObject($"Tentacle_{role}");
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.localPosition = Vector3.zero;

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace      = true;
        lr.positionCount      = 2;
        lr.widthMultiplier    = tentacleWidth;
        lr.shadowCastingMode  = ShadowCastingMode.Off;
        lr.receiveShadows     = false;
        lr.sharedMaterial = tentacleMaterial != null
            ? tentacleMaterial
            : SharedMaterials.SpriteDefault;

        lr.numCornerVertices = 5;
        lr.numCapVertices    = 5;
        lr.widthCurve = new AnimationCurve(
            new Keyframe(0f,    0.3f),
            new Keyframe(0.2f,  1.0f),
            new Keyframe(0.65f, 0.9f),
            new Keyframe(1f,    0.0f)
        );

        lr.SetPosition(0, transform.position);
        lr.SetPosition(1, transform.position);
        lr.enabled = false;

        // Shimmer particle system
        if (shimmerEnabled)
        {
            var shimGo = new GameObject("ShimmerPS");
            shimGo.transform.SetParent(go.transform, worldPositionStays: false);
            var ps = shimGo.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop             = false;
            main.simulationSpace  = ParticleSystemSimulationSpace.World;
            main.startLifetime    = Mathf.Max(0.25f, shimmerLifetime);
            main.startSize        = Mathf.Max(0.04f, shimmerSize);
            main.maxParticles     = 1024;

            Color rc = GetRoleColor(role);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(rc.r, rc.g, rc.b, shimmerAlpha),
                Color.white
            );

            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = false;

            var rend = ps.GetComponent<ParticleSystemRenderer>();
            rend.sharedMaterial   = SharedMaterials.SpriteDefault;
            rend.sortingLayerName = "Foreground";
            rend.sortingOrder     = 40;

            tentacle.shimmerPS = ps;
        }

        return lr;
    }
}
