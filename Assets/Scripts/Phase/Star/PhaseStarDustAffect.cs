using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
public sealed partial class PhaseStarDustAffect : MonoBehaviour
{
    [Header("Zap Cadence")]
    [Tooltip("Hold time on target before a zap is committed.")]
    [SerializeField] private float minContactTime = .01f;
    [Tooltip("Blend duration from seek colors into active drain colors.")]
    [SerializeField] private float colorTransitionTime = 0.5f;

    [Header("Drain Polish")]
    [Tooltip("Anticipation window after contact: the cell shudders/brightens before the drain fires. 0 = legacy instant chip.")]
    [SerializeField, Min(0f)] private float drainBuildupSeconds = 0.35f;
    [Tooltip("Travel time of the siphon energy packet from tentacle tip to the star root.")]
    [SerializeField, Min(0.05f)] private float siphonTravelSeconds = 0.5f;
    [Tooltip("How far the siphon packet color is pushed toward white.")]
    [SerializeField, Range(0f, 1f)] private float siphonBrightness = 0.85f;

    [Header("Tentacle Motion")]
    [Tooltip("Regrow speed for tentacle extension (progress per second).")]
    [SerializeField] private float tentacleGrowSpeed = 0.35f;   // progress/sec (0-1 range)
    [Tooltip("Retract speed after zap resolve or target loss.")]
    [SerializeField] private float tentacleRetractSpeed = 12f;
    [SerializeField] private float tentacleFlowSpeed = 1.2f;
    [SerializeField] private float tentacleWidth = 0.13f;
    [SerializeField] private float dissolveDuration = 0.8f;
    [FormerlySerializedAs("maxLinesPerRole")]
    [SerializeField, Min(1)] private int fallbackTentaclesPerRole = 10;
    [SerializeField] private Material tentacleMaterial;
    [SerializeField, Min(1)] private int invalidTargetRetryFrames = 3;
    [SerializeField, Min(0f)] private float invalidTargetRetryMs = 120f;

    [Header("Line Visual")]
    [SerializeField, Range(0f, 1f)] private float growingRootAlpha  = 0.65f;
    [SerializeField, Range(0f, 1f)] private float growingTipAlpha   = 0.40f;
    [SerializeField, Range(0f, 1f)] private float drainingBaseAlpha = 0.45f;

    [Header("Tether Style")]
    [SerializeField] private float sag        = 0.5f;
    [SerializeField] private float noiseAmp   = 0.15f;
    [SerializeField] private float noiseSpeed = 1.5f;
    [SerializeField] private float weaveAmplitude = 0.1f;
    [SerializeField, Min(0.01f)] private float weaveWavelength = 1.8f;
    [SerializeField] private float weaveSpeed = 1.1f;

    [Header("Shimmer")]
    [SerializeField] private bool  shimmerEnabled     = true;
    [SerializeField] private float shimmerRatePerUnit = 5f;
    [SerializeField] private float shimmerLifetime    = 0.35f;
    [SerializeField] private float shimmerSize        = 0.04f;
    [SerializeField] private float shimmerAlpha       = 0.45f;

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
    }

    public Action<MusicalRole, float> onDelivery;
    public event Action<MusicalRole> OnAttuned;
    public event Action OnAllTentaclesRetracted;

    private GameFlowManager _gfm;
    private PhaseStarBehaviorProfile _profile;
    private PhaseStar _star;
    private PhaseStarCravingNavigator _navigator;
    private MusicalRole _attunedRole = MusicalRole.None;

    [Header("Zap Stagger")]
    [Tooltip("Optional pacing between successive tentacle activations. 0 (default) = every available carved cell grows a tentacle immediately, up to the node payload; > 0 = one activation per interval.")]
    [SerializeField, Min(0f)] private float interTentacleStartInterval = 0f;

    private bool _tentaclesActive;
    private bool _acquisitionEnabled = true;
    private bool _isRetractAllInProgress;
    private readonly List<Tentacle> _tentacles = new();
    private float _lastTentacleStartTime = 0f;

    private float _keepClearTimer;
    private readonly Dictionary<Vector2Int, Tentacle> _reservedCells = new();
    private readonly HashSet<Vector2Int> _zappedThisCycle = new();

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
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

        Vector2 starPos  = transform.position;
        Vector2 targetPt = best.targetWorldPos;
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
        gen.SetStarKeepClear(center, _profile.starKeepClearRadiusCells, forceRemoveExisting: false);
    }

    private void AllocateTentacleBuffers(Tentacle tentacle)
    {
        tentacle.linePts   = new Vector3[SplinePoints];
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
        // Pool = the node payload: one tentacle per note the next MineNode will carry.
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
        lr.material = tentacleMaterial != null
            ? tentacleMaterial
            : new Material(Shader.Find("Sprites/Default"));

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
            rend.material         = new Material(Shader.Find("Sprites/Default"));
            rend.sortingLayerName = "Foreground";
            rend.sortingOrder     = 40;

            tentacle.shimmerPS = ps;
        }

        return lr;
    }
}
