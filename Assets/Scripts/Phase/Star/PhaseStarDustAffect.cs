using System;
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

    [Header("Tentacle")]
    [SerializeField] private float tentacleGrowSpeed = 5f;
    [SerializeField] private float tentacleRetractSpeed = 12f;
    [SerializeField] private float tentacleFlowSpeed = 1.2f;
    [SerializeField] private float tentacleWidth = 0.07f;
    [SerializeField] private float diamondTipOffset = 0.3f;
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
        public bool notifiedDrainLock;
    }

    public Action<MusicalRole, float> onDelivery;

    private PhaseStarBehaviorProfile _profile;
    private PhaseStar _star;
    private PhaseStarCravingNavigator _navigator;

    private bool _tentaclesActive;
    private readonly List<Tentacle> _tentacles = new();
    private readonly List<Tentacle> _branchTentacles = new();

    private float _keepClearTimer;

    private readonly List<Vector2Int> _dustScratch = new(256);
    private readonly List<Vector2Int> _bfsPathScratch = new(128);
    private readonly List<Vector2Int> _simplifiedPathScratch = new(64);
    private readonly Queue<Vector2Int> _bfsQueue = new(512);
    private readonly Dictionary<Vector2Int, Vector2Int> _bfsPrev = new(512);
    private readonly HashSet<Vector2Int> _branchExcludeScratch = new();

    private static readonly Vector2Int[] BfsDirs =
    {
        Vector2Int.right,
        Vector2Int.left,
        Vector2Int.up,
        Vector2Int.down
    };

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;
        _star = star;
        _navigator = GetComponent<PhaseStarCravingNavigator>();

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

        ClearAllTentacleVisuals();
        _tentacles.Clear();

        for (int i = 0; i < roles.Count; i++)
        {
            var tentacle = new Tentacle
            {
                role = roles[i],
                tipIndex = i % 4,
                flowOffset = UnityEngine.Random.value,
                tipPos = transform.position
            };

            AllocateTentacleBuffers(tentacle);
            tentacle.ctrlRebuildTimer = i * (vineCtrlRebuildInterval / Mathf.Max(1, roles.Count));
            tentacle.line = CreateTentacleLine(roles[i]);
            _tentacles.Add(tentacle);
        }

        star.OnDisarmed += _ => SetTentaclesActive(false);
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

        foreach (var tentacle in _tentacles)
            ResetTentacleState(tentacle, starPos, destroyVisual: false);

        ClearBranchTentacles();

        foreach (var tentacle in _tentacles)
            tentacle.childBranch = null;
    }

    public bool IsAnyTentacleDraining
    {
        get
        {
            foreach (var tentacle in _tentacles)
                if (tentacle.state == TentacleState.Draining)
                    return true;

            foreach (var tentacle in _branchTentacles)
                if (tentacle.state == TentacleState.Draining)
                    return true;

            return false;
        }
    }

    public bool HasActiveTentacles
    {
        get
        {
            foreach (var tentacle in _tentacles)
            {
                if (tentacle.state == TentacleState.Growing ||
                    tentacle.state == TentacleState.Draining ||
                    tentacle.state == TentacleState.Dissolving)
                    return true;
            }

            foreach (var tentacle in _branchTentacles)
            {
                if (tentacle.state == TentacleState.Growing ||
                    tentacle.state == TentacleState.Draining ||
                    tentacle.state == TentacleState.Dissolving)
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

        foreach (var tentacle in _tentacles)
        {
            if (tentacle.isSubBranch)
                continue;

            int diamondForTentacle = (tentacle.tipIndex == 0 || tentacle.tipIndex == 2) ? 0 : 1;
            if (diamondForTentacle != diamondIndex)
                continue;

            int priority = tentacle.state == TentacleState.Draining ? 2 :
                           tentacle.state == TentacleState.Growing ? 1 : -1;
            if (priority < 0)
                continue;

            if (priority > bestPriority)
            {
                best = tentacle;
                bestPriority = priority;
            }
        }

        if (best == null)
            return;

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
        if (!_tentaclesActive)
            return;

        float dt = Time.deltaTime;

        _keepClearTimer += dt;
        if (_keepClearTimer >= keepClearTick)
        {
            _keepClearTimer = 0f;
            TickKeepClear();
        }

        foreach (var tentacle in _tentacles)
            TickTentacle(tentacle, dt);

        for (int i = 0; i < _branchTentacles.Count; i++)
            TickTentacle(_branchTentacles[i], dt);

        for (int i = _branchTentacles.Count - 1; i >= 0; i--)
        {
            var branch = _branchTentacles[i];
            if (branch.state != TentacleState.Idle)
                continue;

            if (branch.parentTentacle != null && branch.parentTentacle.childBranch == branch)
                branch.parentTentacle.childBranch = null;

            ReleaseDrainLock(branch);

            if (branch.line != null)
                Destroy(branch.line.gameObject);

            _branchTentacles.RemoveAt(i);
        }
    }

    private void TickTentacle(Tentacle tentacle, float dt)
    {
        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        Vector2 starPos = transform.position;

        switch (tentacle.state)
        {
            case TentacleState.Idle:
            {
                if (tentacle.isSubBranch || gen == null || drum == null || _navigator == null)
                    break;

                if (_navigator.TryGetTargetForRole(tentacle.role, out var cell) && IsTargetValid(cell, tentacle.role))
                    BeginGrowingTentacle(tentacle, cell, drum, starPos);

                break;
            }

            case TentacleState.Growing:
            {
                if (drum != null)
                    tentacle.targetWorldPos = drum.GridToWorldPosition(tentacle.targetCell);

                if (!IsTargetValid(tentacle.targetCell, tentacle.role))
                {
                    if (_navigator != null && drum != null &&
                        _navigator.TryGetTargetForRole(tentacle.role, out var newCell) &&
                        newCell != tentacle.targetCell &&
                        IsTargetValid(newCell, tentacle.role))
                    {
                        tentacle.targetCell = newCell;
                        tentacle.targetWorldPos = drum.GridToWorldPosition(newCell);
                        tentacle.ctrlRebuildTimer = 0f;
                    }
                    else
                    {
                        ReleaseDrainLock(tentacle);
                        tentacle.state = TentacleState.Retracting;
                        break;
                    }
                }

                tentacle.ctrlRebuildTimer -= dt;
                if (tentacle.ctrlRebuildTimer <= 0f)
                {
                    tentacle.ctrlRebuildTimer = vineCtrlRebuildInterval;
                    RebuildBaseControlPts(tentacle, GetLineRoot(tentacle));
                }

                float arcLen = ApproxSplineLength(tentacle.splineBuf, vineSplinePoints);
                tentacle.growProgress = Mathf.Clamp01(
                    tentacle.growProgress + tentacleGrowSpeed * dt / Mathf.Max(0.5f, arcLen));

                UpdateTentacleLine(tentacle);
                ManageBranchSpawn(tentacle);

                if (tentacle.growProgress >= 1f)
                {
                    tentacle.tipPos = tentacle.targetWorldPos;
                    tentacle.state = TentacleState.Draining;
                    tentacle.contactTimer = 0f;
                    tentacle.drainTimer = drainTick;
                    tentacle.ctrlRebuildTimer = 0f;

                    if (!tentacle.notifiedDrainLock)
                    {
                        _navigator?.NotifyDraining(tentacle.targetCell);
                        tentacle.notifiedDrainLock = true;
                    }
                }

                break;
            }

            case TentacleState.Draining:
            {
                if (!IsTargetValid(tentacle.targetCell, tentacle.role))
                {
                    ReleaseDrainLock(tentacle);
                    tentacle.state = TentacleState.Retracting;
                    break;
                }

                tentacle.tipPos = tentacle.targetWorldPos;
                tentacle.drainFlashTimer = Mathf.Max(0f, tentacle.drainFlashTimer - dt);

                tentacle.ctrlRebuildTimer -= dt;
                if (tentacle.ctrlRebuildTimer <= 0f)
                {
                    tentacle.ctrlRebuildTimer = vineCtrlRebuildInterval;
                    RebuildBaseControlPts(tentacle, GetLineRoot(tentacle));
                }

                UpdateTentacleLine(tentacle);
                ManageBranchSpawn(tentacle);

                tentacle.contactTimer += dt;
                if (tentacle.contactTimer >= minContactTime)
                {
                    tentacle.drainTimer += dt;
                    if (tentacle.drainTimer >= drainTick)
                    {
                        tentacle.drainTimer = 0f;
                        DrainTick(tentacle, gen);
                    }
                }

                break;
            }

            case TentacleState.Retracting:
            {
                if (tentacle.childBranch != null && tentacle.childBranch.state != TentacleState.Idle)
                    tentacle.childBranch.state = TentacleState.Retracting;

                ReleaseDrainLock(tentacle);

                Vector2 retractTarget = tentacle.isSubBranch ? GetLineRoot(tentacle) : starPos;
                tentacle.tipPos = Vector2.MoveTowards(tentacle.tipPos, retractTarget, tentacleRetractSpeed * dt);
                UpdateTentacleLine(tentacle);

                if (Vector2.Distance(tentacle.tipPos, retractTarget) < 0.05f)
                {
                    tentacle.state = TentacleState.Idle;
                    tentacle.line.enabled = false;
                }

                break;
            }

            case TentacleState.Dissolving:
            {
                ReleaseDrainLock(tentacle);

                tentacle.dissolveTimer += dt;
                tentacle.drainFlashTimer = Mathf.Max(0f, tentacle.drainFlashTimer - dt);
                tentacle.alphaScale = Mathf.Clamp01(1f - tentacle.dissolveTimer / dissolveDuration);
                tentacle.tipPos = tentacle.targetWorldPos;
                UpdateTentacleLine(tentacle);

                if (tentacle.dissolveTimer >= dissolveDuration)
                {
                    tentacle.state = TentacleState.Idle;
                    tentacle.alphaScale = 1f;
                    tentacle.dissolveTimer = 0f;
                    tentacle.growProgress = 0f;
                    tentacle.line.enabled = false;
                    tentacle.line.widthMultiplier = tentacleWidth;
                }

                break;
            }
        }
    }

    private void BeginGrowingTentacle(Tentacle tentacle, Vector2Int cell, DrumTrack drum, Vector2 starPos)
    {
        tentacle.targetCell = cell;
        tentacle.targetWorldPos = drum.GridToWorldPosition(cell);
        tentacle.tipPos = starPos;
        tentacle.drainTimer = 0f;
        tentacle.growProgress = 0f;
        tentacle.ctrlRebuildTimer = 0f;
        tentacle.contactTimer = 0f;
        tentacle.dissolveTimer = 0f;
        tentacle.alphaScale = 1f;
        tentacle.notifiedDrainLock = false;

        for (int i = 0; i < vineSplinePoints; i++)
            tentacle.splineBuf[i] = starPos;

        tentacle.state = TentacleState.Growing;
        tentacle.line.enabled = true;
        UpdateTentacleLine(tentacle);
    }

    private void DrainTick(Tentacle tentacle, CosmicDustGenerator gen)
    {
        if (gen == null)
            return;

        if (!gen.TryGetDustAt(tentacle.targetCell, out var dust) || dust == null)
            return;

        int chipUnits = Mathf.Max(1, Mathf.RoundToInt(drainRatePerSec * drainTick));
        int actualUnits = dust.ChipEnergy(chipUnits);

        if (actualUnits > 0 && _star != null)
        {
            _star.AddCharge(tentacle.role, actualUnits);
            onDelivery?.Invoke(tentacle.role, actualUnits);
            tentacle.drainFlashTimer = drainFlashDuration;
        }

        if (dust.currentEnergyUnits <= 0)
        {
            gen.ClearCell(
                tentacle.targetCell,
                CosmicDustGenerator.DustClearMode.FadeAndHide,
                fadeSeconds: 1.5f,
                scheduleRegrow: false);

            tentacle.state = TentacleState.Dissolving;
            tentacle.dissolveTimer = 0f;
            tentacle.alphaScale = 1f;
        }
    }

    private void RebuildBaseControlPts(Tentacle tentacle, Vector2 rootPos)
    {
        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null)
            return;

        Vector2Int rootCell = drum.WorldToGridPosition(rootPos);

        if (TryFindGridPath(rootCell, tentacle.targetCell, gen, _bfsPathScratch))
        {
            BuildNavPath(_bfsPathScratch, tentacle, rootPos, drum);
        }
        else
        {
            tentacle.hasNavPath = false;
            BuildControlPtsRepulsionFallback(tentacle, rootPos, gen, drum);
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
                if (_bfsPrev.ContainsKey(next))
                    continue;
                if (gen.HasDustAt(next) && next != end)
                    continue;

                _bfsPrev[next] = cur;
                _bfsQueue.Enqueue(next);
            }
        }

        if (!found)
            return false;

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

    private void BuildNavPath(List<Vector2Int> rawPath, Tentacle tentacle, Vector2 rootPos, DrumTrack drum)
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
        if (tentacle.navWorldPts == null || tentacle.navWorldPts.Length < n)
            tentacle.navWorldPts = new Vector2[Mathf.Max(n, 16)];
        if (tentacle.ctrlBuf == null || tentacle.ctrlBuf.Length < n)
            tentacle.ctrlBuf = new Vector2[Mathf.Max(n, 16)];

        tentacle.navPtCount = n;
        tentacle.hasNavPath = true;
        tentacle.navWorldPts[0] = rootPos;

        for (int i = 1; i < n - 1; i++)
            tentacle.navWorldPts[i] = drum.GridToWorldPosition(_simplifiedPathScratch[i]);

        tentacle.navWorldPts[n - 1] = tentacle.targetWorldPos;
    }

    private void BuildControlPtsRepulsionFallback(Tentacle tentacle, Vector2 rootPos, CosmicDustGenerator gen, DrumTrack drum)
    {
        gen.GetColoredDustCells(_dustScratch);

        Vector2 path = tentacle.targetWorldPos - rootPos;
        float len = path.magnitude;
        Vector2 perp = len > 0.0001f ? new Vector2(-path.y, path.x) / len : Vector2.up;

        for (int i = 0; i < vineCtrlPointCount; i++)
        {
            float baseT = (i + 1f) / (vineCtrlPointCount + 1f);
            Vector2 basePos = rootPos + path * baseT;

            float seed = (float)tentacle.role * 31.4f + i * 7.3f;
            float noiseVal = Mathf.PerlinNoise(seed, Time.time * 0.15f) * 2f - 1f;
            float offset = noiseVal * vineNoiseAmplitude * baseT * (1f - baseT) * 4f;
            tentacle.baseControlPts[i] = basePos + perp * offset;
        }

        for (int cellIndex = 0; cellIndex < _dustScratch.Count; cellIndex++)
        {
            Vector2Int cell = _dustScratch[cellIndex];
            if (cell == tentacle.targetCell)
                continue;

            Vector2 cellWorld = drum.GridToWorldPosition(cell);
            for (int pointIndex = 0; pointIndex < vineCtrlPointCount; pointIndex++)
            {
                Vector2 delta = tentacle.baseControlPts[pointIndex] - cellWorld;
                float dist = delta.magnitude;
                if (dist < vineDustRepulsionRadius && dist > 0.001f)
                {
                    float strength = (1f - dist / vineDustRepulsionRadius) * vineDustRepulsionStrength;
                    tentacle.baseControlPts[pointIndex] += (delta / dist) * strength;
                }
            }
        }
    }

    private void EvaluateVineSpline(Tentacle tentacle, Vector2 rootPos)
    {
        if (tentacle.hasNavPath && tentacle.navPtCount >= 2)
        {
            tentacle.navWorldPts[0] = rootPos;
            tentacle.navWorldPts[tentacle.navPtCount - 1] = tentacle.targetWorldPos;

            for (int i = 0; i < tentacle.navPtCount; i++)
            {
                int prevIndex = Mathf.Max(0, i - 1);
                int nextIndex = Mathf.Min(tentacle.navPtCount - 1, i + 1);
                Vector2 tangent = tentacle.navWorldPts[nextIndex] - tentacle.navWorldPts[prevIndex];
                Vector2 perp = tangent.sqrMagnitude > 0.0001f
                    ? new Vector2(-tangent.y, tangent.x).normalized
                    : Vector2.up;

                float seed = (float)tentacle.role * 31.4f + i * 7.3f;
                float breathe = Mathf.PerlinNoise(seed, Time.time / vineBreathePeriod) * 2f - 1f;
                tentacle.ctrlBuf[i] = tentacle.navWorldPts[i] + perp * (breathe * vineNoiseAmplitude * 0.08f);
            }

            CatmullRomChain(tentacle.ctrlBuf, tentacle.navPtCount, tentacle.splineBuf, vineSplinePoints);
            return;
        }

        Vector2 path = tentacle.targetWorldPos - rootPos;
        float len = path.magnitude;
        Vector2 perpFallback = len > 0.0001f ? new Vector2(-path.y, path.x) / len : Vector2.up;

        tentacle.ctrlBuf[0] = rootPos;
        tentacle.ctrlBuf[vineCtrlPointCount + 1] = tentacle.targetWorldPos;

        for (int i = 0; i < vineCtrlPointCount; i++)
        {
            float seed = (float)tentacle.role * 31.4f + i * 7.3f;
            float breatheVal = Mathf.PerlinNoise(seed, Time.time / vineBreathePeriod) * 2f - 1f;
            tentacle.ctrlBuf[i + 1] = tentacle.baseControlPts[i] + perpFallback * (breatheVal * vineNoiseAmplitude * 0.15f);
        }

        CatmullRomChain(tentacle.ctrlBuf, vineCtrlPointCount + 2, tentacle.splineBuf, vineSplinePoints);
    }

    private Vector2 GetLineRoot(Tentacle tentacle)
    {
        if (tentacle.isSubBranch && tentacle.parentTentacle != null && tentacle.parentTentacle.splineBuf != null)
        {
            int midIndex = Mathf.Clamp(vineSplinePoints / 2, 0, tentacle.parentTentacle.splineBuf.Length - 1);
            return tentacle.parentTentacle.splineBuf[midIndex];
        }

        var diamond = (tentacle.tipIndex == 0 || tentacle.tipIndex == 2)
            ? _star?.PrimaryDiamondTransform
            : _star?.SecondaryDiamondTransform;

        if (diamond == null)
            return transform.position;

        var sr = diamond.GetComponent<SpriteRenderer>();
        float tipDist = (sr != null && sr.sprite != null)
            ? sr.sprite.bounds.extents.y
            : diamondTipOffset;

        float pole = (tentacle.tipIndex < 2) ? 1f : -1f;
        return (Vector2)diamond.position + (Vector2)diamond.up * (tipDist * pole);
    }

    private void UpdateTentacleLine(Tentacle tentacle)
    {
        Vector2 lineRoot = GetLineRoot(tentacle);

        if (tentacle.state == TentacleState.Retracting)
        {
            tentacle.line.positionCount = 2;
            tentacle.line.widthMultiplier = tentacleWidth;
            tentacle.line.SetPosition(0, lineRoot);
            tentacle.line.SetPosition(1, tentacle.tipPos);
            BuildGradient(tentacle, GetRoleColor(tentacle.role));
            tentacle.line.colorGradient = tentacle.gradient;
            return;
        }

        EvaluateVineSpline(tentacle, lineRoot);

        int renderCount = tentacle.state == TentacleState.Growing
            ? Mathf.Max(2, Mathf.RoundToInt(tentacle.growProgress * (vineSplinePoints - 1)) + 1)
            : vineSplinePoints;

        tentacle.line.positionCount = renderCount;
        tentacle.line.widthMultiplier = tentacleWidth * tentacle.alphaScale;

        tentacle.line.SetPosition(0, lineRoot);
        for (int i = 1; i < renderCount; i++)
            tentacle.line.SetPosition(i, tentacle.splineBuf[i]);

        float dustCharge01 = 1f;
        if (tentacle.state == TentacleState.Draining || tentacle.state == TentacleState.Dissolving)
        {
            var gen = GameFlowManager.Instance?.dustGenerator;
            if (gen != null && gen.TryGetDustAt(tentacle.targetCell, out var dustRef) && dustRef != null)
                dustCharge01 = dustRef.Charge01;
        }

        BuildGradient(tentacle, GetRoleColor(tentacle.role), dustCharge01);
        tentacle.line.colorGradient = tentacle.gradient;
    }

    private void BuildGradient(Tentacle tentacle, Color roleColor, float dustCharge01 = 1f)
    {
        float alpha = tentacle.alphaScale;

        if (tentacle.state == TentacleState.Draining)
        {
            float colorFill = Mathf.Clamp01(tentacle.contactTimer / Mathf.Max(0.001f, colorTransitionTime));
            float colorFront = 1f - colorFill;

            if (colorFill < 0.999f)
            {
                float edge0 = Mathf.Max(0f, colorFront - 0.08f);
                float edge1 = Mathf.Min(1f, colorFront + 0.08f);
                Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);

                tentacle.gradient.SetKeys(
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
                        new GradientAlphaKey(0.45f * alpha, 0f),
                        new GradientAlphaKey(0.45f * alpha, edge0),
                        new GradientAlphaKey(0.85f * alpha, edge1),
                        new GradientAlphaKey(dustCharge01 * 0.5f * alpha, 0.92f),
                        new GradientAlphaKey(0f, 1f),
                    }
                );
                return;
            }

            BuildDrainPulseGradient(tentacle, roleColor, alpha, dustCharge01);
            return;
        }

        if (tentacle.state == TentacleState.Dissolving)
        {
            BuildDrainPulseGradient(tentacle, roleColor, alpha, dustCharge01);
            return;
        }

        tentacle.gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.gray, 0f),
                new GradientColorKey(Color.gray, 1f),
            },
            new[]
            {
                new GradientAlphaKey(0.55f * alpha, 0f),
                new GradientAlphaKey(0.08f * alpha, 1f),
            }
        );
    }

    private void BuildDrainPulseGradient(Tentacle tentacle, Color roleColor, float alpha, float dustCharge01 = 1f)
    {
        float rawT = (Time.time * tentacleFlowSpeed + tentacle.flowOffset) % 1f;
        float pulse = 1f - rawT;
        float halfWidth = 0.1f;
        float p0 = Mathf.Clamp01(pulse - halfWidth);
        float p1 = pulse;
        float p2 = Mathf.Clamp01(pulse + halfWidth);

        float flashBoost = tentacle.drainFlashTimer > 0f ? 0.5f : 0.4f;
        Color bright = Color.Lerp(roleColor, Color.white, flashBoost);

        Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);
        float tipAlpha = dustCharge01 * 0.6f * alpha;

        tentacle.gradient.SetKeys(
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
                new GradientAlphaKey(0.35f * alpha, 0f),
                new GradientAlphaKey(0.2f * alpha, p0),
                new GradientAlphaKey(1.0f * alpha, p1),
                new GradientAlphaKey(0.2f * alpha, p2),
                new GradientAlphaKey(tipAlpha, 0.92f),
                new GradientAlphaKey(0f, 1f),
            }
        );
    }

    private void ManageBranchSpawn(Tentacle tentacle)
    {
        if (maxBranchDepth <= 0 ||
            tentacle.childBranch != null ||
            tentacle.branchDepth >= maxBranchDepth ||
            _navigator == null)
            return;

        if (tentacle.state == TentacleState.Growing && tentacle.growProgress < branchSpawnAtProgress)
            return;
        if (tentacle.state != TentacleState.Growing && tentacle.state != TentacleState.Draining)
            return;

        _branchExcludeScratch.Clear();
        var chain = tentacle;
        while (chain != null)
        {
            if (chain.state != TentacleState.Idle)
                _branchExcludeScratch.Add(chain.targetCell);
            chain = chain.parentTentacle;
        }

        if (!_navigator.TryGetTargetForRole(tentacle.role, out var cell, _branchExcludeScratch))
            return;

        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null)
            return;

        var branch = new Tentacle
        {
            role = tentacle.role,
            tipIndex = tentacle.tipIndex,
            isSubBranch = true,
            parentTentacle = tentacle,
            branchDepth = tentacle.branchDepth + 1,
            flowOffset = UnityEngine.Random.value,
            targetCell = cell,
            targetWorldPos = drum.GridToWorldPosition(cell),
            state = TentacleState.Growing
        };

        AllocateTentacleBuffers(branch);
        branch.ctrlRebuildTimer = 0f;
        branch.line = CreateTentacleLine(tentacle.role);

        Vector2 branchRoot = GetLineRoot(branch);
        for (int i = 0; i < vineSplinePoints; i++)
            branch.splineBuf[i] = branchRoot;

        branch.tipPos = branchRoot;
        branch.line.enabled = true;

        tentacle.childBranch = branch;
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
        if (_profile == null || !_profile.starKeepsDustClear)
            return;

        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;
        if (gen == null || drums == null)
            return;

        Vector2Int center = drums.WorldToGridPosition(transform.position);
        gen.SetStarKeepClear(center, _profile.starKeepClearRadiusCells, forceRemoveExisting: false);
    }

    private void AllocateTentacleBuffers(Tentacle tentacle)
    {
        tentacle.baseControlPts = new Vector2[vineCtrlPointCount];
        tentacle.ctrlBuf = new Vector2[Mathf.Max(vineCtrlPointCount + 2, 16)];
        tentacle.splineBuf = new Vector2[vineSplinePoints];
        tentacle.gradient = new Gradient();
        tentacle.alphaScale = 1f;
        tentacle.navWorldPts = null;
        tentacle.navPtCount = 0;
        tentacle.hasNavPath = false;
        tentacle.notifiedDrainLock = false;
    }

    private void ResetTentacleState(Tentacle tentacle, Vector2 starPos, bool destroyVisual)
    {
        ReleaseDrainLock(tentacle);
        tentacle.state = TentacleState.Idle;
        tentacle.tipPos = starPos;
        tentacle.growProgress = 0f;
        tentacle.contactTimer = 0f;
        tentacle.dissolveTimer = 0f;
        tentacle.alphaScale = 1f;
        tentacle.drainTimer = 0f;
        tentacle.ctrlRebuildTimer = 0f;
        tentacle.drainFlashTimer = 0f;
        tentacle.hasNavPath = false;
        tentacle.navPtCount = 0;
        tentacle.childBranch = null;

        if (tentacle.line != null)
        {
            if (destroyVisual)
                Destroy(tentacle.line.gameObject);
            else
            {
                tentacle.line.enabled = false;
                tentacle.line.widthMultiplier = tentacleWidth;
            }
        }
    }

    private void ReleaseDrainLock(Tentacle tentacle)
    {
        if (!tentacle.notifiedDrainLock)
            return;

        _navigator?.ClearLockOn(tentacle.targetCell);
        tentacle.notifiedDrainLock = false;
    }

    private void ClearBranchTentacles()
    {
        for (int i = 0; i < _branchTentacles.Count; i++)
        {
            var branch = _branchTentacles[i];
            if (branch == null)
                continue;
            ResetTentacleState(branch, transform.position, destroyVisual: true);
        }

        _branchTentacles.Clear();
    }

    private void ClearAllTentacleVisuals()
    {
        ClearBranchTentacles();
        foreach (var tentacle in _tentacles)
        {
            if (tentacle?.line != null)
                Destroy(tentacle.line.gameObject);
        }
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
            float globalT = outCount > 1 ? (float)i / (outCount - 1) : 0f;
            float spanF = globalT * spans;
            int spanIdx = Mathf.Min((int)spanF, spans - 1);
            float localT = spanF - spanIdx;

            Vector2 p0 = spanIdx == 0 ? 2f * ctrl[0] - ctrl[1] : ctrl[spanIdx - 1];
            Vector2 p1 = ctrl[spanIdx];
            Vector2 p2 = ctrl[spanIdx + 1];
            Vector2 p3 = spanIdx + 2 >= ctrlCount
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
        if (gen == null || !gen.HasDustAt(cell))
            return false;
        if (!gen.TryGetDustAt(cell, out var dust) || dust == null)
            return false;

        return dust.Role == role && dust.currentEnergyUnits > 0;
    }
}
