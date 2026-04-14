using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    [Header("Keep-Clear Pocket (optional)")]
    [SerializeField] private float keepClearTick = 0.15f;

    [Header("Drain")]
    [SerializeField] private float drainTick = 0.08f;
    [SerializeField] private float drainRatePerSec = 1.0f;
    [SerializeField] private float minContactTime = 1.0f;
    [SerializeField] private float colorTransitionTime = 0.5f;
    [SerializeField] private float drainFlashDuration = 0.08f;
    [SerializeField] private float starDrainRegrowDelay = 6f;

    [Header("Tentacle")]
    [SerializeField] private float tentacleGrowSpeed = 5f;
    [SerializeField] private float tentacleRetractSpeed = 12f;
    [SerializeField] private float tentacleFlowSpeed = 1.2f;
    [SerializeField] private float tentacleWidth = 0.07f;
    [SerializeField] private float diamondTipOffset = 0.3f;
    [SerializeField, Min(1f)] private float rootDirSmoothSpeed = 8f;
    [SerializeField] private float dissolveDuration = 0.8f;
    [SerializeField] private Material tentacleMaterial;

    [Header("Vine Spline")]
    [SerializeField] private int vineCtrlPointCount = 4;
    [SerializeField] private int vineSplinePoints = 16;
    [SerializeField] private float vineNoiseAmplitude = 0.6f;
    [SerializeField] private float vineBreathePeriod = 2.2f;
    [SerializeField] private float vineDustRepulsionRadius = 1.8f;
    [SerializeField] private float vineDustRepulsionStrength = 0.9f;
    [SerializeField] private float vineCtrlRebuildInterval = 0.15f;

    [Header("Branches")]
    [SerializeField, Range(0f, 1f)] private float branchSpawnAtProgress = 0.4f;
    [SerializeField, Min(0)] private int maxBranchDepth = 3;

    private enum TentacleState { Idle, Growing, Draining, Retracting, Dissolving }

    private class Tentacle
    {
        public MusicalRole role;
        public int tipIndex;
        public TentacleState state = TentacleState.Idle;
        public Vector2Int targetCell;
        public Vector2 targetWorldPos;
        public Vector2 tipPos;
        public LineRenderer line;
        public float drainTimer;
        public float flowOffset;

        public Vector2[] baseControlPts;
        public Vector2[] ctrlBuf;
        public Vector2[] splineBuf;
        public Gradient gradient;

        public Vector2[] navWorldPts;
        public int navPtCount;
        public bool hasNavPath;

        public float ctrlRebuildTimer;
        public float growProgress;

        public bool isSubBranch;
        public Tentacle parentTentacle;
        public Tentacle childBranch;
        public int branchDepth;

        public float contactTimer;
        public float dissolveTimer;
        public float alphaScale = 1f;
        public float drainFlashTimer;
        public Vector2 smoothedRootDir;

        public bool notifiedDrainLock;
    }

    public System.Action<MusicalRole, float> onDelivery;

    private PhaseStarBehaviorProfile _profile;
    private PhaseStar _star;
    private PhaseStarCravingNavigator _navigator;
    private PhaseStarMotion2D _motion;

    private bool _tentaclesActive;
    private readonly List<Tentacle> _tentacles = new();

    private float _keepClearTimer;

    private readonly List<Vector2Int> _dustScratch = new(256);
    private readonly List<Vector2Int> _bfsPathScratch = new(128);
    private readonly List<Vector2Int> _simplifiedPathScratch = new(64);
    private readonly Queue<Vector2Int> _bfsQueue = new(512);
    private readonly Dictionary<Vector2Int, Vector2Int> _bfsPrev = new(512);

    private static readonly Vector2Int[] BfsDirs =
    {
        Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down
    };

    private readonly List<Tentacle> _branchTentacles = new();
    private readonly HashSet<Vector2Int> _branchExcludeScratch = new();

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;
        _star = star;
        _navigator = GetComponent<PhaseStarCravingNavigator>();
        _motion = GetComponent<PhaseStarMotion2D>();

        var roles = star.GetMotifActiveRoles();
        if (roles == null || roles.Count == 0)
        {
            roles = new List<MusicalRole>
            {
                MusicalRole.Bass,
                MusicalRole.Harmony,
                MusicalRole.Lead,
                MusicalRole.Groove
            };
        }

        _tentacles.Clear();

        for (int i = 0; i < roles.Count; i++)
        {
            var t = new Tentacle
            {
                role = roles[i],
                tipIndex = i % 4,
                flowOffset = Random.value,
                tipPos = transform.position
            };

            AllocateTentacleBuffers(t);
            t.ctrlRebuildTimer = i * (vineCtrlRebuildInterval / Mathf.Max(1, roles.Count));
            t.line = CreateTentacleLine(roles[i]);
            _tentacles.Add(t);
        }

        star.OnDisarmed += _ => SetTentaclesActive(false);
    }

    private void AllocateTentacleBuffers(Tentacle t)
    {
        t.baseControlPts = new Vector2[vineCtrlPointCount];
        t.ctrlBuf = new Vector2[Mathf.Max(vineCtrlPointCount + 2, 16)];
        t.splineBuf = new Vector2[vineSplinePoints];
        t.gradient = new Gradient();
        t.alphaScale = 1f;
        t.navWorldPts = null;
        t.navPtCount = 0;
        t.hasNavPath = false;
        t.notifiedDrainLock = false;
    }

    public void SetTentaclesActive(bool active)
    {
        _tentaclesActive = active;
        _navigator?.SetHuntingEnabled(active);

        if (!active)
            ResetTentacles();
    }

    public void ResetTentacles()
    {
        Vector2 starPos = transform.position;

        foreach (var t in _tentacles)
        {
            if (t.notifiedDrainLock)
            {
                _navigator?.ClearLockOn(t.targetCell);
                t.notifiedDrainLock = false;
            }

            t.state = TentacleState.Idle;
            t.tipPos = starPos;
            t.growProgress = 0f;
            t.contactTimer = 0f;
            t.dissolveTimer = 0f;
            t.alphaScale = 1f;
            t.drainTimer = 0f;
            t.ctrlRebuildTimer = 0f;
            t.drainFlashTimer = 0f;
            t.smoothedRootDir = Vector2.zero;
            t.hasNavPath = false;
            t.navPtCount = 0;
            t.line.enabled = false;
            t.line.widthMultiplier = tentacleWidth;
        }

        for (int i = 0; i < _branchTentacles.Count; i++)
        {
            var bt = _branchTentacles[i];
            if (bt.notifiedDrainLock)
                _navigator?.ClearLockOn(bt.targetCell);

            if (bt.line != null)
                Destroy(bt.line.gameObject);
        }

        _branchTentacles.Clear();

        foreach (var t in _tentacles)
            t.childBranch = null;

        _motion?.SetFrozen(false);
    }

    public bool IsAnyTentacleDraining
    {
        get
        {
            foreach (var t in _tentacles)
                if (t.state == TentacleState.Draining) return true;

            foreach (var t in _branchTentacles)
                if (t.state == TentacleState.Draining) return true;

            return false;
        }
    }

    public bool HasActiveTentacles
    {
        get
        {
            foreach (var t in _tentacles)
            {
                if (t.state == TentacleState.Growing ||
                    t.state == TentacleState.Draining ||
                    t.state == TentacleState.Dissolving)
                    return true;
            }

            foreach (var t in _branchTentacles)
            {
                if (t.state == TentacleState.Growing ||
                    t.state == TentacleState.Draining ||
                    t.state == TentacleState.Dissolving)
                    return true;
            }

            return false;
        }
    }

    public void GetDiamondLockState(int diamondIndex, out bool locked, out float lockAngleDeg, out bool isDraining)
    {
        locked = false;
        lockAngleDeg = 0f;
        isDraining = false;

        Tentacle best = null;
        int bestPriority = -1;

        foreach (var t in _tentacles)
        {
            if (t.isSubBranch) continue;

            int di = (t.tipIndex == 0 || t.tipIndex == 2) ? 0 : 1;
            if (di != diamondIndex) continue;

            int pri = t.state == TentacleState.Draining ? 2 :
                      t.state == TentacleState.Growing ? 1 : -1;

            if (pri < 0) continue;

            if (pri > bestPriority)
            {
                best = t;
                bestPriority = pri;
            }
        }

        if (best == null) return;

        locked = true;
        isDraining = best.state == TentacleState.Draining;

        var diamond = diamondIndex == 0
            ? _star?.PrimaryDiamondTransform
            : _star?.SecondaryDiamondTransform;

        Vector2 center = diamond != null ? (Vector2)diamond.position : (Vector2)transform.position;
        Vector2 dir = best.targetWorldPos - center;

        float baseAngle = dir.sqrMagnitude > 0.0001f
            ? Mathf.Atan2(-dir.x, dir.y) * Mathf.Rad2Deg
            : 0f;

        lockAngleDeg = (best.tipIndex >= 2) ? baseAngle + 180f : baseAngle;
    }

    private void Update()
    {
        if (!_tentaclesActive) return;

        float dt = Time.deltaTime;

        _keepClearTimer += dt;
        if (_keepClearTimer >= keepClearTick)
        {
            _keepClearTimer = 0f;
            TickKeepClear();
        }

        foreach (var t in _tentacles)
            TickTentacle(t, dt);

        for (int bi = 0; bi < _branchTentacles.Count; bi++)
            TickTentacle(_branchTentacles[bi], dt);

        for (int bi = _branchTentacles.Count - 1; bi >= 0; bi--)
        {
            var bt = _branchTentacles[bi];
            if (bt.state != TentacleState.Idle) continue;

            if (bt.parentTentacle != null && bt.parentTentacle.childBranch == bt)
                bt.parentTentacle.childBranch = null;

            if (bt.notifiedDrainLock)
                _navigator?.ClearLockOn(bt.targetCell);

            if (bt.line != null)
                Destroy(bt.line.gameObject);

            _branchTentacles.RemoveAt(bi);
        }

        _motion?.SetFrozen(false);
    }

    private void TickTentacle(Tentacle t, float dt)
    {
        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;

        Vector2 starPos = transform.position;

        switch (t.state)
        {
            case TentacleState.Idle:
            {
                if (t.isSubBranch) break;
                if (gen == null || drums == null) return;
                if (_navigator == null) return;

                if (_navigator.TryGetTargetForRole(t.role, out var cell) && IsTargetValid(cell, t.role))
                {
                    t.targetCell = cell;
                    t.targetWorldPos = drums.GridToWorldPosition(cell);
                    t.tipPos = starPos;
                    t.drainTimer = 0f;
                    t.growProgress = 0f;
                    t.ctrlRebuildTimer = 0f;
                    t.contactTimer = 0f;
                    t.dissolveTimer = 0f;
                    t.alphaScale = 1f;
                    t.notifiedDrainLock = false;

                    for (int i = 0; i < vineSplinePoints; i++)
                        t.splineBuf[i] = starPos;

                    t.state = TentacleState.Growing;
                    t.line.enabled = true;
                    UpdateTentacleLine(t, starPos);
                }

                break;
            }

            case TentacleState.Growing:
            {
                if (drums != null)
                    t.targetWorldPos = drums.GridToWorldPosition(t.targetCell);

                if (!IsTargetValid(t.targetCell, t.role))
                {
                    if (_navigator != null &&
                        drums != null &&
                        _navigator.TryGetTargetForRole(t.role, out var newCell) &&
                        newCell != t.targetCell &&
                        IsTargetValid(newCell, t.role))
                    {
                        t.targetCell = newCell;
                        t.targetWorldPos = drums.GridToWorldPosition(newCell);
                        t.ctrlRebuildTimer = 0f;
                    }
                    else
                    {
                        if (t.notifiedDrainLock)
                        {
                            _navigator?.ClearLockOn(t.targetCell);
                            t.notifiedDrainLock = false;
                        }

                        t.state = TentacleState.Retracting;
                        break;
                    }
                }

                t.ctrlRebuildTimer -= dt;
                if (t.ctrlRebuildTimer <= 0f)
                {
                    t.ctrlRebuildTimer = vineCtrlRebuildInterval;
                    RebuildBaseControlPts(t, GetLineRoot(t));
                }

                float arcLen = ApproxSplineLength(t.splineBuf, vineSplinePoints);
                t.growProgress = Mathf.Clamp01(
                    t.growProgress + tentacleGrowSpeed * dt / Mathf.Max(0.5f, arcLen));

                UpdateTentacleLine(t, starPos);
                ManageBranchSpawn(t);

                if (t.growProgress >= 1f)
                {
                    t.tipPos = t.targetWorldPos;
                    t.state = TentacleState.Draining;
                    t.contactTimer = 0f;
                    t.drainTimer = drainTick;
                    t.ctrlRebuildTimer = 0f;

                    if (!t.notifiedDrainLock)
                    {
                        _navigator?.NotifyDraining(t.targetCell);
                        t.notifiedDrainLock = true;
                    }
                }

                break;
            }

            case TentacleState.Draining:
            {
                if (!IsTargetValid(t.targetCell, t.role))
                {
                    if (t.notifiedDrainLock)
                    {
                        _navigator?.ClearLockOn(t.targetCell);
                        t.notifiedDrainLock = false;
                    }

                    t.state = TentacleState.Retracting;
                    break;
                }

                t.tipPos = t.targetWorldPos;
                t.drainFlashTimer = Mathf.Max(0f, t.drainFlashTimer - dt);

                t.ctrlRebuildTimer -= dt;
                if (t.ctrlRebuildTimer <= 0f)
                {
                    t.ctrlRebuildTimer = vineCtrlRebuildInterval;
                    RebuildBaseControlPts(t, GetLineRoot(t));
                }

                UpdateTentacleLine(t, starPos);
                ManageBranchSpawn(t);

                t.contactTimer += dt;
                if (t.contactTimer >= minContactTime)
                {
                    t.drainTimer += dt;
                    if (t.drainTimer >= drainTick)
                    {
                        t.drainTimer = 0f;
                        DrainTick(t, gen);
                    }
                }

                break;
            }

            case TentacleState.Retracting:
            {
                if (t.childBranch != null && t.childBranch.state != TentacleState.Idle)
                    t.childBranch.state = TentacleState.Retracting;

                if (t.notifiedDrainLock)
                {
                    _navigator?.ClearLockOn(t.targetCell);
                    t.notifiedDrainLock = false;
                }

                Vector2 retractTarget = t.isSubBranch ? GetLineRoot(t) : starPos;
                t.tipPos = Vector2.MoveTowards(t.tipPos, retractTarget, tentacleRetractSpeed * dt);
                UpdateTentacleLine(t, starPos);

                if (Vector2.Distance(t.tipPos, retractTarget) < 0.05f)
                {
                    t.state = TentacleState.Idle;
                    t.line.enabled = false;
                }

                break;
            }

            case TentacleState.Dissolving:
            {
                if (t.notifiedDrainLock)
                {
                    _navigator?.ClearLockOn(t.targetCell);
                    t.notifiedDrainLock = false;
                }

                t.dissolveTimer += dt;
                t.drainFlashTimer = Mathf.Max(0f, t.drainFlashTimer - dt);
                t.alphaScale = Mathf.Clamp01(1f - t.dissolveTimer / dissolveDuration);
                t.tipPos = t.targetWorldPos;
                UpdateTentacleLine(t, starPos);

                if (t.dissolveTimer >= dissolveDuration)
                {
                    t.state = TentacleState.Idle;
                    t.alphaScale = 1f;
                    t.dissolveTimer = 0f;
                    t.growProgress = 0f;
                    t.line.enabled = false;
                    t.line.widthMultiplier = tentacleWidth;
                }

                break;
            }
        }
    }

    private void DrainTick(Tentacle t, CosmicDustGenerator gen)
    {
        if (gen == null) return;
        if (!gen.TryGetDustAt(t.targetCell, out var dust) || dust == null) return;

        int chipUnits = Mathf.Max(1, Mathf.RoundToInt(drainRatePerSec * drainTick));
        int actualUnits = dust.ChipEnergy(chipUnits);

        if (actualUnits > 0 && _star != null)
        {
            _star.AddCharge(t.role, actualUnits);
            onDelivery?.Invoke(t.role, actualUnits);
            t.drainFlashTimer = drainFlashDuration;
        }

        if (dust.currentEnergyUnits <= 0)
        {
            gen.ClearCell(
                t.targetCell,
                CosmicDustGenerator.DustClearMode.FadeAndHide,
                fadeSeconds: 1.5f,
                scheduleRegrow: false);

            t.state = TentacleState.Dissolving;
            t.dissolveTimer = 0f;
            t.alphaScale = 1f;
        }
    }

    private void RebuildBaseControlPts(Tentacle t, Vector2 rootPos)
    {
        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        Vector2Int rootCell = drum.WorldToGridPosition(rootPos);

        if (TryFindGridPath(rootCell, t.targetCell, gen, _bfsPathScratch))
            BuildNavPath(_bfsPathScratch, t, rootPos, drum);
        else
        {
            t.hasNavPath = false;
            BuildControlPtsRepulsionFallback(t, rootPos, gen, drum);
        }
    }

    private bool TryFindGridPath(Vector2Int start, Vector2Int end, CosmicDustGenerator gen, List<Vector2Int> outPath, int budget = 400)
    {
        outPath.Clear();
        if (start == end)
        {
            outPath.Add(start);
            return true;
        }

        _bfsQueue.Clear();
        _bfsPrev.Clear();
        _bfsQueue.Enqueue(start);
        _bfsPrev[start] = start;

        int expanded = 0;
        bool found = false;

        while (_bfsQueue.Count > 0 && expanded < budget)
        {
            var cur = _bfsQueue.Dequeue();
            expanded++;

            if (cur == end)
            {
                found = true;
                break;
            }

            for (int d = 0; d < BfsDirs.Length; d++)
            {
                var next = cur + BfsDirs[d];
                if (_bfsPrev.ContainsKey(next)) continue;
                if (gen.HasDustAt(next) && next != end) continue;

                _bfsPrev[next] = cur;
                _bfsQueue.Enqueue(next);
            }
        }

        if (!found) return false;

        var cell = end;
        while (cell != start)
        {
            outPath.Add(cell);
            cell = _bfsPrev[cell];
        }

        outPath.Add(start);
        outPath.Reverse();
        return true;
    }

    private void BuildNavPath(List<Vector2Int> rawPath, Tentacle t, Vector2 rootPos, DrumTrack drum)
    {
        _simplifiedPathScratch.Clear();
        _simplifiedPathScratch.Add(rawPath[0]);

        for (int i = 1; i < rawPath.Count - 1; i++)
        {
            if (rawPath[i] - rawPath[i - 1] != rawPath[i + 1] - rawPath[i])
                _simplifiedPathScratch.Add(rawPath[i]);
        }

        _simplifiedPathScratch.Add(rawPath[rawPath.Count - 1]);

        int n = _simplifiedPathScratch.Count;

        if (t.navWorldPts == null || t.navWorldPts.Length < n)
            t.navWorldPts = new Vector2[Mathf.Max(n, 16)];

        if (t.ctrlBuf == null || t.ctrlBuf.Length < n)
            t.ctrlBuf = new Vector2[Mathf.Max(n, 16)];

        t.navPtCount = n;
        t.hasNavPath = true;

        t.navWorldPts[0] = rootPos;
        for (int i = 1; i < n - 1; i++)
            t.navWorldPts[i] = drum.GridToWorldPosition(_simplifiedPathScratch[i]);
        t.navWorldPts[n - 1] = t.targetWorldPos;
    }

    private void BuildControlPtsRepulsionFallback(Tentacle t, Vector2 rootPos, CosmicDustGenerator gen, DrumTrack drum)
    {
        gen.GetColoredDustCells(_dustScratch);

        Vector2 path = t.targetWorldPos - rootPos;
        float len = path.magnitude;
        Vector2 perp = len > 0.0001f ? new Vector2(-path.y, path.x) / len : Vector2.up;

        for (int i = 0; i < vineCtrlPointCount; i++)
        {
            float baseT = (i + 1f) / (vineCtrlPointCount + 1f);
            Vector2 basePos = rootPos + path * baseT;

            float seed = (float)t.role * 31.4f + i * 7.3f;
            float noiseVal = Mathf.PerlinNoise(seed, Time.time * 0.15f) * 2f - 1f;
            float offset = noiseVal * vineNoiseAmplitude * baseT * (1f - baseT) * 4f;

            t.baseControlPts[i] = basePos + perp * offset;
        }

        for (int ci = 0; ci < _dustScratch.Count; ci++)
        {
            Vector2Int cell = _dustScratch[ci];
            if (cell == t.targetCell) continue;

            Vector2 cellWorld = drum.GridToWorldPosition(cell);

            for (int pi = 0; pi < vineCtrlPointCount; pi++)
            {
                Vector2 delta = t.baseControlPts[pi] - cellWorld;
                float dist = delta.magnitude;
                if (dist < vineDustRepulsionRadius && dist > 0.001f)
                {
                    float strength = (1f - dist / vineDustRepulsionRadius) * vineDustRepulsionStrength;
                    t.baseControlPts[pi] += (delta / dist) * strength;
                }
            }
        }
    }

    private void EvaluateVineSpline(Tentacle t, Vector2 rootPos)
    {
        if (t.hasNavPath && t.navPtCount >= 2)
        {
            t.navWorldPts[0] = rootPos;
            t.navWorldPts[t.navPtCount - 1] = t.targetWorldPos;

            for (int i = 0; i < t.navPtCount; i++)
            {
                int pi = Mathf.Max(0, i - 1);
                int ni = Mathf.Min(t.navPtCount - 1, i + 1);
                Vector2 tan = t.navWorldPts[ni] - t.navWorldPts[pi];
                Vector2 perp = tan.sqrMagnitude > 0.0001f
                    ? new Vector2(-tan.y, tan.x).normalized
                    : Vector2.up;

                float seed = (float)t.role * 31.4f + i * 7.3f;
                float breathe = Mathf.PerlinNoise(seed, Time.time / vineBreathePeriod) * 2f - 1f;
                t.ctrlBuf[i] = t.navWorldPts[i] + perp * (breathe * vineNoiseAmplitude * 0.08f);
            }

            CatmullRomChain(t.ctrlBuf, t.navPtCount, t.splineBuf, vineSplinePoints);
        }
        else
        {
            Vector2 path = t.targetWorldPos - rootPos;
            float len = path.magnitude;
            Vector2 perp = len > 0.0001f ? new Vector2(-path.y, path.x) / len : Vector2.up;

            t.ctrlBuf[0] = rootPos;
            t.ctrlBuf[vineCtrlPointCount + 1] = t.targetWorldPos;

            for (int i = 0; i < vineCtrlPointCount; i++)
            {
                float seed = (float)t.role * 31.4f + i * 7.3f;
                float breatheVal = Mathf.PerlinNoise(seed, Time.time / vineBreathePeriod) * 2f - 1f;
                t.ctrlBuf[i + 1] = t.baseControlPts[i] + perp * (breatheVal * vineNoiseAmplitude * 0.15f);
            }

            CatmullRomChain(t.ctrlBuf, vineCtrlPointCount + 2, t.splineBuf, vineSplinePoints);
        }
    }

    private Vector2 GetLineRoot(Tentacle t)
    {
        if (t.isSubBranch && t.parentTentacle != null && t.parentTentacle.splineBuf != null)
        {
            int midIdx = Mathf.Clamp(vineSplinePoints / 2, 0, t.parentTentacle.splineBuf.Length - 1);
            return t.parentTentacle.splineBuf[midIdx];
        }

        var diamond = (t.tipIndex == 0 || t.tipIndex == 2)
            ? _star?.PrimaryDiamondTransform
            : _star?.SecondaryDiamondTransform;

        if (diamond == null) return transform.position;

        var sr = diamond.GetComponent<SpriteRenderer>();
        float tipDist = (sr != null && sr.sprite != null)
            ? sr.sprite.bounds.extents.y
            : diamondTipOffset;

        float pole = (t.tipIndex < 2) ? 1f : -1f;
        return (Vector2)diamond.position + (Vector2)diamond.up * (tipDist * pole);
    }

    private void UpdateTentacleLine(Tentacle t, Vector2 starPos)
    {
        Vector2 lineRoot = GetLineRoot(t);

        if (t.state == TentacleState.Retracting)
        {
            t.line.positionCount = 2;
            t.line.widthMultiplier = tentacleWidth;
            t.line.SetPosition(0, lineRoot);
            t.line.SetPosition(1, t.tipPos);
            BuildGradient(t, GetRoleColor(t.role));
            t.line.colorGradient = t.gradient;
            return;
        }

        EvaluateVineSpline(t, lineRoot);

        int renderCount = (t.state == TentacleState.Growing)
            ? Mathf.Max(2, Mathf.RoundToInt(t.growProgress * (vineSplinePoints - 1)) + 1)
            : vineSplinePoints;

        t.line.positionCount = renderCount;
        t.line.widthMultiplier = tentacleWidth * t.alphaScale;

        t.line.SetPosition(0, lineRoot);
        for (int i = 1; i < renderCount; i++)
            t.line.SetPosition(i, t.splineBuf[i]);

        float dustCharge01 = 1f;
        if (t.state == TentacleState.Draining || t.state == TentacleState.Dissolving)
        {
            var gen = GameFlowManager.Instance?.dustGenerator;
            if (gen != null && gen.TryGetDustAt(t.targetCell, out var dustRef) && dustRef != null)
                dustCharge01 = dustRef.Charge01;
        }

        BuildGradient(t, GetRoleColor(t.role), dustCharge01);
        t.line.colorGradient = t.gradient;
    }

    private void BuildGradient(Tentacle t, Color roleColor, float dustCharge01 = 1f)
    {
        float a = t.alphaScale;

        if (t.state == TentacleState.Draining)
        {
            float colorFill = Mathf.Clamp01(t.contactTimer / Mathf.Max(0.001f, colorTransitionTime));
            float colorFront = 1f - colorFill;

            if (colorFill < 0.999f)
            {
                float edge0 = Mathf.Max(0f, colorFront - 0.08f);
                float edge1 = Mathf.Min(1f, colorFront + 0.08f);
                Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);

                t.gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(Color.gray, 0f),
                        new GradientColorKey(Color.gray, edge0),
                        new GradientColorKey(roleColor, edge1),
                        new GradientColorKey(dustTipColor, 0.92f),
                        new GradientColorKey(dustTipColor, 1f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(0.45f * a, 0f),
                        new GradientAlphaKey(0.45f * a, edge0),
                        new GradientAlphaKey(0.85f * a, edge1),
                        new GradientAlphaKey(dustCharge01 * 0.5f * a, 0.92f),
                        new GradientAlphaKey(0f, 1f),
                    }
                );
                return;
            }

            BuildDrainPulseGradient(t, roleColor, a, dustCharge01);
        }
        else if (t.state == TentacleState.Dissolving)
        {
            BuildDrainPulseGradient(t, roleColor, a, dustCharge01);
        }
        else
        {
            t.gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.gray, 0f),
                    new GradientColorKey(Color.gray, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.55f * a, 0f),
                    new GradientAlphaKey(0.08f * a, 1f),
                }
            );
        }
    }

    private void BuildDrainPulseGradient(Tentacle t, Color roleColor, float a, float dustCharge01 = 1f)
    {
        float rawT = (Time.time * tentacleFlowSpeed + t.flowOffset) % 1f;
        float pulse = 1f - rawT;
        float hw = 0.1f;
        float p0 = Mathf.Clamp01(pulse - hw);
        float p1 = pulse;
        float p2 = Mathf.Clamp01(pulse + hw);

        float flashBoost = t.drainFlashTimer > 0f ? 0.5f : 0.4f;
        Color bright = Color.Lerp(roleColor, Color.white, flashBoost);

        Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);
        float tipAlpha = dustCharge01 * 0.6f * a;

        t.gradient.SetKeys(
            new[]
            {
                new GradientColorKey(roleColor, 0f),
                new GradientColorKey(bright, p1),
                new GradientColorKey(roleColor, p2),
                new GradientColorKey(dustTipColor, 0.92f),
                new GradientColorKey(dustTipColor, 1f),
            },
            new[]
            {
                new GradientAlphaKey(0.35f * a, 0f),
                new GradientAlphaKey(0.2f * a, p0),
                new GradientAlphaKey(1.0f * a, p1),
                new GradientAlphaKey(0.2f * a, p2),
                new GradientAlphaKey(tipAlpha, 0.92f),
                new GradientAlphaKey(0f, 1f),
            }
        );
    }

    private static Color GetRoleColor(MusicalRole role)
    {
        var rp = MusicalRoleProfileLibrary.GetProfile(role);
        return rp != null
            ? new Color(rp.dustColors.baseColor.r, rp.dustColors.baseColor.g, rp.dustColors.baseColor.b, 1f)
            : Color.white;
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    private static void CatmullRomChain(Vector2[] ctrl, int ctrlCount, Vector2[] outPts, int outCount)
    {
        int spans = ctrlCount - 1;

        for (int i = 0; i < outCount; i++)
        {
            float globalT = (outCount > 1) ? (float)i / (outCount - 1) : 0f;
            float spanF = globalT * spans;
            int spanIdx = Mathf.Min((int)spanF, spans - 1);
            float localT = spanF - spanIdx;

            Vector2 p0 = (spanIdx == 0) ? 2f * ctrl[0] - ctrl[1] : ctrl[spanIdx - 1];
            Vector2 p1 = ctrl[spanIdx];
            Vector2 p2 = ctrl[spanIdx + 1];
            Vector2 p3 = (spanIdx + 2 >= ctrlCount)
                ? 2f * ctrl[ctrlCount - 1] - ctrl[ctrlCount - 2]
                : ctrl[spanIdx + 2];

            outPts[i] = CatmullRom(p0, p1, p2, p3, localT);
        }
    }

    private static float ApproxSplineLength(Vector2[] pts, int count)
    {
        float total = 0f;
        for (int i = 1; i < count; i++)
            total += Vector2.Distance(pts[i - 1], pts[i]);
        return total;
    }

    private bool IsTargetValid(Vector2Int cell, MusicalRole role)
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null) return false;
        if (!gen.HasDustAt(cell)) return false;
        if (!gen.TryGetDustAt(cell, out var dust) || dust == null) return false;
        return dust.Role == role && dust.currentEnergyUnits > 0;
    }

    private void ManageBranchSpawn(Tentacle t)
    {
        if (maxBranchDepth <= 0) return;
        if (t.childBranch != null) return;
        if (t.branchDepth >= maxBranchDepth) return;
        if (t.state == TentacleState.Growing && t.growProgress < branchSpawnAtProgress) return;
        if (t.state != TentacleState.Growing && t.state != TentacleState.Draining) return;
        if (_navigator == null) return;

        _branchExcludeScratch.Clear();
        var chain = t;
        while (chain != null)
        {
            if (chain.state != TentacleState.Idle)
                _branchExcludeScratch.Add(chain.targetCell);

            chain = chain.parentTentacle;
        }

        if (!_navigator.TryGetTargetForRole(t.role, out var cell, _branchExcludeScratch))
            return;

        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null) return;

        var branch = new Tentacle
        {
            role = t.role,
            tipIndex = t.tipIndex,
            isSubBranch = true,
            parentTentacle = t,
            branchDepth = t.branchDepth + 1,
            flowOffset = Random.value,
            targetCell = cell,
            targetWorldPos = drum.GridToWorldPosition(cell),
            state = TentacleState.Growing
        };

        AllocateTentacleBuffers(branch);
        branch.ctrlRebuildTimer = 0f;
        branch.line = CreateTentacleLine(t.role);

        Vector2 branchRoot = GetLineRoot(branch);
        for (int i = 0; i < vineSplinePoints; i++)
            branch.splineBuf[i] = branchRoot;

        branch.tipPos = branchRoot;
        branch.line.enabled = true;

        t.childBranch = branch;
        _branchTentacles.Add(branch);
    }

    private LineRenderer CreateTentacleLine(MusicalRole role)
    {
        var go = new GameObject($"Tentacle_{role}");
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.localPosition = Vector3.zero;

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.widthMultiplier = tentacleWidth;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.material = tentacleMaterial != null
            ? tentacleMaterial
            : new Material(Shader.Find("Sprites/Default"));

        lr.numCornerVertices = 5;
        lr.numCapVertices = 5;

        lr.widthCurve = new AnimationCurve(
            new Keyframe(0f, 0.3f),
            new Keyframe(0.2f, 1.0f),
            new Keyframe(0.65f, 0.9f),
            new Keyframe(1f, 0.0f)
        );

        lr.SetPosition(0, transform.position);
        lr.SetPosition(1, transform.position);
        lr.enabled = false;
        return lr;
    }

    private void TickKeepClear()
    {
        if (_profile == null || !_profile.starKeepsDustClear) return;

        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;
        if (gen == null || drums == null) return;

        Vector2Int center = drums.WorldToGridPosition(transform.position);
        gen.SetStarKeepClear(center, _profile.starKeepClearRadiusCells, forceRemoveExisting: false);
    }
}