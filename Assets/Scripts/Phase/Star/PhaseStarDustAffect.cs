using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    [Header("Drain")]
    [SerializeField] private float drainRatePerSec = 5.0f;
    [SerializeField] private float minContactTime = .01f;
    [SerializeField] private float colorTransitionTime = 0.5f;

    [Header("Tentacle")]
    [SerializeField] private float tentacleGrowSpeed = 0.35f;   // progress/sec (0-1 range)
    [SerializeField] private float tentacleRetractSpeed = 12f;
    [SerializeField] private float tentacleFlowSpeed = 1.2f;
    [SerializeField] private float tentacleWidth = 0.13f;
    [SerializeField] private float dissolveDuration = 0.8f;
    [SerializeField, Min(1)] private int maxLinesPerRole = 3;
    [SerializeField] private Material tentacleMaterial;

    [Header("Line Visual")]
    [SerializeField, Range(0f, 1f)] private float growingRootAlpha  = 0.65f;
    [SerializeField, Range(0f, 1f)] private float growingTipAlpha   = 0.40f;
    [SerializeField, Range(0f, 1f)] private float drainingBaseAlpha = 0.45f;

    [Header("Tether Style")]
    [SerializeField] private float sag        = 0.5f;
    [SerializeField] private float noiseAmp   = 0.15f;
    [SerializeField] private float noiseSpeed = 1.5f;

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
        public float drainTimer;
        public float flowOffset;
        public Vector3[] linePts;
        public Gradient gradient;
        public float growProgress;
        public float contactTimer;
        public float dissolveTimer;
        public float alphaScale = 1f;
        public float drainFlashTimer;
        public bool notifiedDrainLock;
    }

    public Action<MusicalRole, float> onDelivery;
    public event Action<MusicalRole> OnAttuned;

    private PhaseStarBehaviorProfile _profile;
    private PhaseStar _star;
    private PhaseStarCravingNavigator _navigator;
    private MusicalRole _attunedRole = MusicalRole.None;

    private bool _tentaclesActive;
    private readonly List<Tentacle> _tentacles = new();

    private float _keepClearTimer;

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _attunedRole = MusicalRole.None;
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

        int slotIndex = 0;
        for (int i = 0; i < roles.Count; i++)
        {
            for (int j = 0; j < maxLinesPerRole; j++, slotIndex++)
            {
                var tentacle = new Tentacle
                {
                    role       = roles[i],
                    tipIndex   = slotIndex % 4,
                    flowOffset = UnityEngine.Random.value,
                    tipPos     = transform.position
                };

                AllocateTentacleBuffers(tentacle);
                tentacle.line = CreateTentacleLine(roles[i], tentacle);
                _tentacles.Add(tentacle);
            }
        }

        star.OnDisarmed += _ => SetTentaclesActive(false);
    }

    public void SetAttunedRole(MusicalRole role)
    {
        _attunedRole = role;
        ResetTentacles();
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
    }

    public bool IsAnyTentacleDraining
    {
        get
        {
            foreach (var tentacle in _tentacles)
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
                if (tentacle.state == TentacleState.Growing  ||
                    tentacle.state == TentacleState.Draining ||
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

        var drum = GameFlowManager.Instance?.activeDrumTrack;
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

        _keepClearTimer += dt;
        if (_keepClearTimer >= KeepClearTick)
        {
            _keepClearTimer = 0f;
            TickKeepClear();
        }

        foreach (var tentacle in _tentacles)
            TickTentacle(tentacle, dt);
    }

    private void TickTentacle(Tentacle tentacle, float dt)
    {
        var gfm    = GameFlowManager.Instance;
        var gen    = gfm?.dustGenerator;
        var drum   = gfm?.activeDrumTrack;
        Vector2 starPos = transform.position;

        switch (tentacle.state)
        {
            case TentacleState.Idle:
            {
                if (gen == null || drum == null || _navigator == null) break;
                if (_attunedRole != MusicalRole.None && tentacle.role != _attunedRole) break;

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
                        tentacle.targetCell      = newCell;
                        tentacle.targetWorldPos  = drum.GridToWorldPosition(newCell);
                    }
                    else
                    {
                        ReleaseDrainLock(tentacle);
                        tentacle.state = TentacleState.Retracting;
                        break;
                    }
                }

                tentacle.growProgress = Mathf.Clamp01(tentacle.growProgress + tentacleGrowSpeed * dt);
                UpdateTentacleLine(tentacle, starPos, dt);

                if (tentacle.growProgress >= 1f)
                {
                    tentacle.tipPos      = tentacle.targetWorldPos;
                    tentacle.state       = TentacleState.Draining;
                    tentacle.contactTimer = 0f;
                    tentacle.drainTimer  = 0f;

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

                tentacle.tipPos        = tentacle.targetWorldPos;
                tentacle.drainFlashTimer = Mathf.Max(0f, tentacle.drainFlashTimer - dt);

                UpdateTentacleLine(tentacle, starPos, dt);

                tentacle.contactTimer += dt;
                if (tentacle.contactTimer >= minContactTime)
                {
                    tentacle.drainTimer += drainRatePerSec * dt;
                    while (tentacle.drainTimer >= 1f)
                    {
                        tentacle.drainTimer -= 1f;
                        DrainTick(tentacle, gen);
                    }
                }

                break;
            }

            case TentacleState.Retracting:
            {
                ReleaseDrainLock(tentacle);

                tentacle.tipPos = Vector2.MoveTowards(tentacle.tipPos, starPos, tentacleRetractSpeed * dt);
                UpdateTentacleRetractLine(tentacle, starPos);

                if (Vector2.Distance(tentacle.tipPos, starPos) < 0.05f)
                {
                    tentacle.state      = TentacleState.Idle;
                    tentacle.line.enabled = false;
                }

                break;
            }

            case TentacleState.Dissolving:
            {
                ReleaseDrainLock(tentacle);

                tentacle.dissolveTimer  += dt;
                tentacle.drainFlashTimer = Mathf.Max(0f, tentacle.drainFlashTimer - dt);
                tentacle.alphaScale      = Mathf.Clamp01(1f - tentacle.dissolveTimer / dissolveDuration);
                tentacle.tipPos          = tentacle.targetWorldPos;
                UpdateTentacleLine(tentacle, starPos, dt);

                if (tentacle.dissolveTimer >= dissolveDuration)
                {
                    tentacle.state        = TentacleState.Idle;
                    tentacle.alphaScale   = 1f;
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
        tentacle.targetCell     = cell;
        tentacle.targetWorldPos = drum.GridToWorldPosition(cell);
        tentacle.tipPos         = starPos;
        tentacle.drainTimer     = 0f;
        tentacle.growProgress   = 0f;
        tentacle.contactTimer   = 0f;
        tentacle.dissolveTimer  = 0f;
        tentacle.alphaScale     = 1f;
        tentacle.notifiedDrainLock = false;

        Vector3 root3 = new Vector3(starPos.x, starPos.y, transform.position.z);
        for (int i = 0; i < SplinePoints; i++)
            tentacle.linePts[i] = root3;

        tentacle.state        = TentacleState.Growing;
        tentacle.line.enabled = true;
        UpdateTentacleLine(tentacle, starPos, 0f);
    }

    private void DrainTick(Tentacle tentacle, CosmicDustGenerator gen)
    {
        if (gen == null) return;

        if (!gen.TryGetDustAt(tentacle.targetCell, out var dust) || dust == null)
            return;

        int actualUnits = dust.ChipEnergy(1);

        if (actualUnits > 0 && _star != null)
        {
            bool wasUnattned = _star.AttunedRole == MusicalRole.None;
            _star.AddCharge(tentacle.role, actualUnits);
            onDelivery?.Invoke(tentacle.role, actualUnits);
            tentacle.drainFlashTimer = DrainFlashDuration;
            if (wasUnattned)
                OnAttuned?.Invoke(tentacle.role);
        }

        if (dust.currentEnergyUnits <= 0)
        {
            gen.ClearCell(
                tentacle.targetCell,
                CosmicDustGenerator.DustClearMode.FadeAndHide,
                fadeSeconds: 1.5f,
                scheduleRegrow: false);

            tentacle.state        = TentacleState.Dissolving;
            tentacle.dissolveTimer = 0f;
            tentacle.alphaScale   = 1f;
        }
    }

    // ---------------------------------------------------------------------------
    // NoteTether-style Bezier rendering
    // ---------------------------------------------------------------------------

    private void RebuildNoteTetherCurve(Tentacle tentacle, Vector2 starPos)
    {
        var cam = Camera.main;
        Vector3 aW = new Vector3(starPos.x, starPos.y, transform.position.z);
        Vector3 dW = new Vector3(tentacle.targetWorldPos.x, tentacle.targetWorldPos.y, transform.position.z);

        if (cam == null)
        {
            for (int i = 0; i < SplinePoints; i++)
            {
                float u = i / (float)(SplinePoints - 1);
                tentacle.linePts[i] = Vector3.Lerp(aW, dW, u);
            }
            return;
        }

        float depth = cam.orthographic
            ? 0f
            : Mathf.Abs(aW.z - cam.transform.position.z);

        Vector3 aS = cam.WorldToScreenPoint(new Vector3(aW.x, aW.y, cam.orthographic ? 0f : aW.z));
        Vector3 dS = cam.WorldToScreenPoint(new Vector3(dW.x, dW.y, cam.orthographic ? 0f : dW.z));
        aS.z = depth;
        dS.z = depth;

        Vector2 delta = (Vector2)(dS - aS);
        float dist = delta.magnitude;
        if (dist < 1f) dist = 1f;

        float sagPx = sag * 120f;
        sagPx = Mathf.Clamp(sagPx, 12f, 160f);

        Vector2 dir = delta / dist;
        Vector2 n   = new Vector2(-dir.y, dir.x);

        const float margin = 24f;
        float midY = (aS.y + dS.y) * 0.5f;
        float upY  = midY + sagPx;
        float dnY  = midY - sagPx;
        float top  = Screen.height - margin;
        float bot  = margin;

        float bendSign = 1f;
        if      (upY > top && dnY >= bot) bendSign = -1f;
        else if (dnY < bot && upY <= top) bendSign =  1f;
        else bendSign = (midY < Screen.height * 0.5f) ? 1f : -1f;

        Vector2 bend   = n * (sagPx * bendSign);
        float   c1Dist = Mathf.Clamp(dist * 0.10f, 18f, 120f);
        float   c2Dist = Mathf.Clamp(dist * 0.12f, 22f, 160f);

        Vector2 bS = (Vector2)aS + dir * c1Dist + bend;
        Vector2 cS = (Vector2)dS - dir * c2Dist + bend * 0.35f;

        float time = Time.time * noiseSpeed + tentacle.flowOffset * 10f;

        for (int i = 0; i < SplinePoints; i++)
        {
            float u  = i / (float)(SplinePoints - 1);
            float t  = Mathf.Pow(u, 2.2f);
            float it = 1f - t;

            Vector2 pS =
                it * it * it * (Vector2)aS +
                3f * it * it * t * bS +
                3f * it * t  * t * cS +
                t  * t  * t  * (Vector2)dS;

            float wob      = (Mathf.PerlinNoise(i * 0.17f, time) - 0.5f) * 2f;
            float wobTaper = 1f;
            const float wobOffAt = 0.80f;
            if (u > wobOffAt)
                wobTaper = 1f - Mathf.InverseLerp(wobOffAt, 1f, u);

            pS += n * wob * (noiseAmp * 35f) * wobTaper;
            pS.x = Mathf.Clamp(pS.x, margin, Screen.width  - margin);
            pS.y = Mathf.Clamp(pS.y, margin, Screen.height - margin);

            Vector3 pW = cam.ScreenToWorldPoint(new Vector3(pS.x, pS.y, depth));
            pW.z = aW.z;
            tentacle.linePts[i] = pW;
        }
    }

    private static float ComputeCurveLength(Vector3[] pts)
    {
        float len = 0f;
        for (int i = 1; i < pts.Length; i++)
            len += Vector3.Distance(pts[i - 1], pts[i]);
        return len;
    }

    private void UpdateTentacleLine(Tentacle tentacle, Vector2 starPos, float dt)
    {
        RebuildNoteTetherCurve(tentacle, starPos);

        Color roleColor = GetRoleColor(tentacle.role);

        if (tentacle.state == TentacleState.Growing)
        {
            float fullT    = tentacle.growProgress * (SplinePoints - 1);
            int   baseIdx  = Mathf.Min(Mathf.FloorToInt(fullT), SplinePoints - 2);
            float frac     = fullT - baseIdx;
            int renderCount = Mathf.Max(2, baseIdx + 2);

            tentacle.line.positionCount = renderCount;
            for (int i = 0; i < renderCount - 1; i++)
                tentacle.line.SetPosition(i, tentacle.linePts[i]);
            tentacle.line.SetPosition(renderCount - 1,
                Vector3.Lerp(tentacle.linePts[baseIdx], tentacle.linePts[baseIdx + 1], frac));

            BuildGrowingGradient(tentacle, roleColor);
            tentacle.line.colorGradient = tentacle.gradient;
        }
        else
        {
            // Draining or Dissolving — full curve
            tentacle.line.positionCount = SplinePoints;
            tentacle.line.SetPositions(tentacle.linePts);

            float dustCharge01 = 1f;
            if (tentacle.state == TentacleState.Draining || tentacle.state == TentacleState.Dissolving)
            {
                var gen = GameFlowManager.Instance?.dustGenerator;
                if (gen != null && gen.TryGetDustAt(tentacle.targetCell, out var dustRef) && dustRef != null)
                    dustCharge01 = dustRef.Charge01;
            }

            BuildGradient(tentacle, roleColor, dustCharge01);
            tentacle.line.colorGradient = tentacle.gradient;
        }

        tentacle.line.widthMultiplier = tentacleWidth * tentacle.alphaScale;

        if (shimmerEnabled && tentacle.shimmerPS != null &&
            (tentacle.state == TentacleState.Growing || tentacle.state == TentacleState.Draining))
        {
            EmitShimmerForTentacle(tentacle, dt);
        }
    }

    private void UpdateTentacleRetractLine(Tentacle tentacle, Vector2 starPos)
    {
        tentacle.line.positionCount = 2;
        tentacle.line.widthMultiplier = tentacleWidth;
        tentacle.line.SetPosition(0, new Vector3(starPos.x, starPos.y, transform.position.z));
        tentacle.line.SetPosition(1, new Vector3(tentacle.tipPos.x, tentacle.tipPos.y, transform.position.z));
        BuildGrowingGradient(tentacle, GetRoleColor(tentacle.role));
        tentacle.line.colorGradient = tentacle.gradient;
    }

    // ---------------------------------------------------------------------------
    // Gradients
    // ---------------------------------------------------------------------------

    private void BuildGrowingGradient(Tentacle tentacle, Color roleColor)
    {
        float alpha = tentacle.alphaScale;

        tentacle.gradient.SetKeys(
            new[]
            {
                new GradientColorKey(roleColor * 0.6f, 0f),
                new GradientColorKey(Color.white,      0.5f),
                new GradientColorKey(roleColor,        1f),
            },
            new[]
            {
                new GradientAlphaKey(growingRootAlpha * alpha * 0.1f, 0f),
                new GradientAlphaKey(growingRootAlpha * alpha,        0.12f),
                new GradientAlphaKey(drainingBaseAlpha * alpha,       0.5f),
                new GradientAlphaKey(growingTipAlpha * alpha * 0.7f,  0.88f),
                new GradientAlphaKey(0f,                              1f),
            }
        );
    }

    private void BuildGradient(Tentacle tentacle, Color roleColor, float dustCharge01 = 1f)
    {
        float alpha = tentacle.alphaScale;

        if (tentacle.state == TentacleState.Draining)
        {
            float colorFill  = Mathf.Clamp01(tentacle.contactTimer / Mathf.Max(0.001f, colorTransitionTime));
            float colorFront = 1f - colorFill;

            if (colorFill < 0.999f)
            {
                float edge0 = Mathf.Max(0f, colorFront - 0.08f);
                float edge1 = Mathf.Min(1f, colorFront + 0.08f);
                Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);

                tentacle.gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(roleColor * 0.6f, 0f),
                        new GradientColorKey(Color.white,      edge0),
                        new GradientColorKey(roleColor,        edge1),
                        new GradientColorKey(dustTipColor,     0.92f),
                        new GradientColorKey(dustTipColor,     1f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(0.1f * alpha,                    0f),
                        new GradientAlphaKey(drainingBaseAlpha * alpha,       edge0),
                        new GradientAlphaKey(0.85f * alpha,                   edge1),
                        new GradientAlphaKey(dustCharge01 * 0.5f * alpha,     0.92f),
                        new GradientAlphaKey(0f,                              1f),
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

        BuildGrowingGradient(tentacle, roleColor);
    }

    private void BuildDrainPulseGradient(Tentacle tentacle, Color roleColor, float alpha, float dustCharge01 = 1f)
    {
        float rawT     = (Time.time * tentacleFlowSpeed + tentacle.flowOffset) % 1f;
        float pulse    = 1f - rawT;
        float halfWidth = 0.1f;
        float p0 = Mathf.Clamp01(pulse - halfWidth);
        float p1 = pulse;
        float p2 = Mathf.Clamp01(pulse + halfWidth);

        float flashBoost = tentacle.drainFlashTimer > 0f ? 0.5f : 0.4f;
        Color bright     = Color.Lerp(roleColor, Color.white, flashBoost);

        Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);
        float tipAlpha     = dustCharge01 * 0.6f * alpha;

        tentacle.gradient.SetKeys(
            new[]
            {
                new GradientColorKey(roleColor,    0f),
                new GradientColorKey(bright,       p1),
                new GradientColorKey(roleColor,    p2),
                new GradientColorKey(dustTipColor, 0.92f),
                new GradientColorKey(dustTipColor, 1f),
            },
            new[]
            {
                new GradientAlphaKey(drainingBaseAlpha * alpha,        0f),
                new GradientAlphaKey(drainingBaseAlpha * 0.5f * alpha, p0),
                new GradientAlphaKey(1.0f * alpha,                     p1),
                new GradientAlphaKey(drainingBaseAlpha * 0.5f * alpha, p2),
                new GradientAlphaKey(tipAlpha,                         0.92f),
                new GradientAlphaKey(0f,                               1f),
            }
        );
    }

    // ---------------------------------------------------------------------------
    // Shimmer
    // ---------------------------------------------------------------------------

    private void EmitShimmerForTentacle(Tentacle tentacle, float dt)
    {
        if (tentacle.shimmerPS == null || tentacle.linePts == null) return;

        float curveLen     = ComputeCurveLength(tentacle.linePts);
        float toEmit       = curveLen * shimmerRatePerUnit * Mathf.Max(0.0001f, dt);
        int   emitCount    = Mathf.FloorToInt(toEmit);
        Color roleColor    = GetRoleColor(tentacle.role);

        for (int i = 0; i < emitCount; i++)
        {
            int   idx = Mathf.Clamp(Mathf.RoundToInt(UnityEngine.Random.value * (SplinePoints - 1)), 0, SplinePoints - 1);
            Vector3 pos = tentacle.linePts[idx] + (Vector3)(UnityEngine.Random.insideUnitCircle * 0.025f);

            var ep = new ParticleSystem.EmitParams
            {
                position     = pos,
                startLifetime = shimmerLifetime * UnityEngine.Random.Range(0.8f, 1.2f),
                startSize    = shimmerSize * UnityEngine.Random.Range(0.8f, 1.3f),
                startColor   = new Color(roleColor.r, roleColor.g, roleColor.b,
                                         shimmerAlpha * UnityEngine.Random.Range(0.6f, 1f))
            };
            tentacle.shimmerPS.Emit(ep, 1);
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

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

    private void TickKeepClear()
    {
        if (_profile == null || !_profile.starKeepsDustClear)
            return;

        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
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
    }

    private void ResetTentacleState(Tentacle tentacle, Vector2 starPos, bool destroyVisual)
    {
        ReleaseDrainLock(tentacle);
        tentacle.state        = TentacleState.Idle;
        tentacle.tipPos       = starPos;
        tentacle.growProgress = 0f;
        tentacle.contactTimer = 0f;
        tentacle.dissolveTimer = 0f;
        tentacle.alphaScale   = 1f;
        tentacle.drainTimer   = 0f;
        tentacle.drainFlashTimer = 0f;

        if (tentacle.line != null)
        {
            if (destroyVisual)
                Destroy(tentacle.line.gameObject);
            else
            {
                tentacle.line.enabled         = false;
                tentacle.line.widthMultiplier = tentacleWidth;
            }
        }
    }

    private void ReleaseDrainLock(Tentacle tentacle)
    {
        if (!tentacle.notifiedDrainLock) return;
        _navigator?.ClearLockOn(tentacle.targetCell);
        tentacle.notifiedDrainLock = false;
    }

    private void ClearAllTentacleVisuals()
    {
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

    private bool IsTargetValid(Vector2Int cell, MusicalRole role)
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null || !gen.HasDustAt(cell)) return false;
        if (!gen.TryGetDustAt(cell, out var dust) || dust == null) return false;
        return dust.Role == role && dust.currentEnergyUnits > 0;
    }
}
