using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Manages the PhaseStar's tentacle system.
///
/// One tentacle line per MusicalRole in the motif grows from the stationary star
/// toward the nearest dust cell of that role. The line follows an organic Catmull-Rom
/// spline that curves away from other CosmicDust. When the tip reaches the cell,
/// the vine breathes for a brief contact delay before energy begins draining. The
/// animated role-color gradient flows back toward the star during drain. When fully
/// drained, the line dissolves in place. If a target becomes invalid mid-flight, the
/// line snaps back (Retracting).
///
/// Star motion is frozen while any tentacle is Growing, Draining, or Dissolving.
/// </summary>
[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    // ---------------------------------------------------------------
    // Inspector
    // ---------------------------------------------------------------
    [Header("Keep-Clear Pocket (optional)")]
    [SerializeField] private float keepClearTick = 0.15f;

    [Header("Drain")]
    [Tooltip("How often the drain probe fires (seconds).")]
    [SerializeField] private float drainTick = 0.08f;

    [Tooltip("Energy units drained per second from the target cell.")]
    [SerializeField] private float drainRatePerSec = 1.0f;

    [Tooltip("Minimum seconds the tentacle must be attached before energy drain begins.")]
    [SerializeField] private float minContactTime = 1.0f;

    [Tooltip("Seconds for the role-color front to sweep from tip to root on arrival.")]
    [SerializeField] private float colorTransitionTime = 0.5f;

    [Tooltip("Seconds the tentacle tip flashes bright after each energy chip.")]
    [SerializeField] private float drainFlashDuration = 0.08f;

    [Tooltip("Regrow delay after a tentacle fully drains a cell.")]
    [SerializeField] private float starDrainRegrowDelay = 6f;

    [Header("Tentacle")]
    [Tooltip("World units per second while the tentacle tip grows toward the target.")]
    [SerializeField] private float tentacleGrowSpeed = 5f;

    [Tooltip("World units per second while the tentacle tip retracts back to the star.")]
    [SerializeField] private float tentacleRetractSpeed = 12f;

    [Tooltip("How many drain-pulse cycles per second animate along the line during draining.")]
    [SerializeField] private float tentacleFlowSpeed = 1.2f;

    [Tooltip("Width of the tentacle line in world units.")]
    [SerializeField] private float tentacleWidth = 0.07f;

    [Tooltip("World units to offset the line root from the star center toward the target, " +
             "so the vine appears to emerge from the nearest diamond tip rather than the center.")]
    [SerializeField] private float diamondTipOffset = 0.3f;

    [Tooltip("How quickly the vine root swings to face the target. Higher = crisper tracking, lower = lazy appendage feel.")]
    [SerializeField, Min(1f)] private float rootDirSmoothSpeed = 8f;

    [Tooltip("Seconds for the tentacle to dissolve in place after fully draining a cell.")]
    [SerializeField] private float dissolveDuration = 0.8f;

    [Tooltip("Optional material for tentacle LineRenderers. Falls back to Sprites/Default.")]
    [SerializeField] private Material tentacleMaterial;

    [Header("Vine Spline")]
    [Tooltip("Number of intermediate control points between star and target (3–6 recommended).")]
    [SerializeField] private int vineCtrlPointCount = 4;

    [Tooltip("Number of LineRenderer positions along the Catmull-Rom spline.")]
    [SerializeField] private int vineSplinePoints = 16;

    [Tooltip("Max perpendicular Perlin-noise offset on control points (world units).")]
    [SerializeField] private float vineNoiseAmplitude = 0.6f;

    [Tooltip("Seconds per Perlin noise breathe cycle (subtle per-frame wiggle on top of base).")]
    [SerializeField] private float vineBreathePeriod = 2.2f;

    [Tooltip("World-unit radius within which non-target colored dust repels control points.")]
    [SerializeField] private float vineDustRepulsionRadius = 1.8f;

    [Tooltip("Max repulsion push strength at contact (world units).")]
    [SerializeField] private float vineDustRepulsionStrength = 0.9f;

    [Tooltip("Interval (seconds) between obstacle-pushed base-point recomputations.")]
    [SerializeField] private float vineCtrlRebuildInterval = 0.15f;

    // ---------------------------------------------------------------
    // Tentacle state machine
    // ---------------------------------------------------------------
    private enum TentacleState { Idle, Growing, Draining, Retracting, Dissolving }

    private class Tentacle
    {
        public MusicalRole   role;
        public TentacleState state         = TentacleState.Idle;
        public Vector2Int    targetCell;
        public Vector2       targetWorldPos;
        public Vector2       tipPos;
        public LineRenderer  line;
        public float         drainTimer;
        public float         flowOffset;       // per-tentacle phase so lines don't pulse in sync

        // Spline buffers — allocated once in AllocateTentacleBuffers
        public Vector2[] baseControlPts;       // [vineCtrlPointCount] obstacle-pushed base positions
        public Vector2[] ctrlBuf;              // [vineCtrlPointCount + 2] star + intermediates + target
        public Vector2[] splineBuf;            // [vineSplinePoints] final sampled spline positions
        public Gradient  gradient;             // cached Gradient (reused via SetKeys to avoid GC)

        public float ctrlRebuildTimer;         // countdown to next obstacle-pushed base recompute

        // Growing — how far along the spline the vine has reached (0..1)
        public float growProgress;

        // Drain gate — vine must be attached this long before energy chips
        public float contactTimer;

        // Dissolve
        public float dissolveTimer;
        public float alphaScale = 1f;          // multiplied into all gradient alpha keys

        // Drain flash — > 0 for drainFlashDuration seconds after each energy chip
        public float drainFlashTimer;

        // Smoothed world-space direction from diamond center toward current target.
        // Lerped each frame so the vine root swings toward the dust rather than jumping.
        public Vector2 smoothedRootDir;
    }

    // ---------------------------------------------------------------
    // Fired when energy units are delivered to PhaseStar.
    // Payload: (role, rawEnergyUnits).
    // ---------------------------------------------------------------
    public System.Action<MusicalRole, float> onDelivery;

    // ---------------------------------------------------------------
    // Internal references
    // ---------------------------------------------------------------
    private PhaseStarBehaviorProfile  _profile;
    private PhaseStar                 _star;
    private PhaseStarCravingNavigator _navigator;
    private PhaseStarMotion2D         _motion;

    private bool           _tentaclesActive;
    private List<Tentacle> _tentacles = new();

    private float _keepClearTimer;

    // Scratch list for dust obstacle queries (populated inside RebuildBaseControlPts)
    private readonly List<Vector2Int> _dustScratch = new(256);

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile   = profile;
        _star      = star;
        _navigator = GetComponent<PhaseStarCravingNavigator>();
        _motion    = GetComponent<PhaseStarMotion2D>();

        // Build one tentacle per motif role.
        var roles = star.GetMotifActiveRoles();
        if (roles == null || roles.Count == 0)
            roles = new List<MusicalRole>
                { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };

        _tentacles.Clear();
        for (int i = 0; i < roles.Count; i++)
        {
            var t = new Tentacle
            {
                role       = roles[i],
                flowOffset = Random.value,
                tipPos     = transform.position,
            };
            AllocateTentacleBuffers(t);
            // Stagger rebuild timers so not all tentacles query the grid on the same frame.
            t.ctrlRebuildTimer = i * (vineCtrlRebuildInterval / Mathf.Max(1, roles.Count));
            t.line = CreateTentacleLine(roles[i]);
            _tentacles.Add(t);
        }

        star.OnDisarmed += _ => SetTentaclesActive(false);
    }

    private void AllocateTentacleBuffers(Tentacle t)
    {
        t.baseControlPts = new Vector2[vineCtrlPointCount];
        t.ctrlBuf        = new Vector2[vineCtrlPointCount + 2];
        t.splineBuf      = new Vector2[vineSplinePoints];
        t.gradient       = new Gradient();
        t.alphaScale     = 1f;
    }

    /// <summary>
    /// Enables or disables all tentacles. Called by PhaseStar on arm (true) and disarm (false).
    /// </summary>
    public void SetTentaclesActive(bool active)
    {
        _tentaclesActive = active;
        if (!active) ResetTentacles();
    }

    /// <summary>
    /// Immediately snaps all tentacles back to the star and sets them Idle.
    /// Called on MineNode ejection and disarm.
    /// </summary>
    public void ResetTentacles()
    {
        Vector2 starPos = transform.position;
        foreach (var t in _tentacles)
        {
            t.state                = TentacleState.Idle;
            t.tipPos               = starPos;
            t.growProgress         = 0f;
            t.contactTimer         = 0f;
            t.dissolveTimer        = 0f;
            t.alphaScale           = 1f;
            t.drainTimer           = 0f;
            t.ctrlRebuildTimer     = 0f;
            t.drainFlashTimer      = 0f;
            t.smoothedRootDir      = Vector2.zero;
            t.line.enabled         = false;
            t.line.widthMultiplier = tentacleWidth;
        }
        _motion?.SetFrozen(false);
    }

    /// <summary>True while any tentacle is actively draining a cell.</summary>
    public bool IsAnyTentacleDraining
    {
        get
        {
            foreach (var t in _tentacles)
                if (t.state == TentacleState.Draining) return true;
            return false;
        }
    }

    /// <summary>True while any tentacle is Growing, Draining, or Dissolving.</summary>
    public bool HasActiveTentacles
    {
        get
        {
            foreach (var t in _tentacles)
                if (t.state == TentacleState.Growing
                 || t.state == TentacleState.Draining
                 || t.state == TentacleState.Dissolving)
                    return true;
            return false;
        }
    }

    // ---------------------------------------------------------------
    // Unity
    // ---------------------------------------------------------------
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

        bool anyActive = false;
        foreach (var t in _tentacles)
        {
            TickTentacle(t, dt);
            if (t.state == TentacleState.Growing
             || t.state == TentacleState.Draining
             || t.state == TentacleState.Dissolving)
                anyActive = true;
        }

        _motion?.SetFrozen(anyActive);
    }

    // ---------------------------------------------------------------
    // Per-tentacle tick
    // ---------------------------------------------------------------
    private void TickTentacle(Tentacle t, float dt)
    {
        var gfm   = GameFlowManager.Instance;
        var gen   = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;

        Vector2 starPos = transform.position;

        switch (t.state)
        {
            case TentacleState.Idle:
            {
                if (gen == null || drums == null) return;
                if (_navigator == null) return;

                if (_navigator.TryGetTargetForRole(t.role, out var cell)
                    && IsTargetValid(cell, t.role))
                {
                    t.targetCell       = cell;
                    t.targetWorldPos   = drums.GridToWorldPosition(cell);
                    t.tipPos           = starPos;
                    t.drainTimer       = 0f;
                    t.growProgress     = 0f;
                    t.ctrlRebuildTimer = 0f; // force immediate base rebuild on first frame

                    // Pre-fill splineBuf so arc-length is valid before EvaluateVineSpline runs.
                    for (int i = 0; i < vineSplinePoints; i++)
                        t.splineBuf[i] = starPos;

                    t.state          = TentacleState.Growing;
                    t.line.enabled   = true;
                    UpdateTentacleLine(t, starPos);
                }
                break;
            }

            case TentacleState.Growing:
            {
                if (drums != null)
                    t.targetWorldPos = drums.GridToWorldPosition(t.targetCell);

                // Validate target — redirect or retract if it's gone.
                if (!IsTargetValid(t.targetCell, t.role))
                {
                    if (_navigator != null && drums != null
                        && _navigator.TryGetTargetForRole(t.role, out var newCell)
                        && newCell != t.targetCell && IsTargetValid(newCell, t.role))
                    {
                        t.targetCell       = newCell;
                        t.targetWorldPos   = drums.GridToWorldPosition(newCell);
                        t.ctrlRebuildTimer = 0f; // redirect — rebuild base immediately
                    }
                    else
                    {
                        t.state = TentacleState.Retracting;
                        break;
                    }
                }

                // Rebuild obstacle-pushed base control points on timer.
                t.ctrlRebuildTimer -= dt;
                if (t.ctrlRebuildTimer <= 0f)
                {
                    t.ctrlRebuildTimer = vineCtrlRebuildInterval;
                    RebuildBaseControlPts(t, starPos);
                }

                // Advance growth: speed in world-units/sec along the spline arc.
                // Use last frame's splineBuf for arc length (valid from frame 2 onward;
                // frame 1 is clamped so the vine doesn't jump to completion).
                float arcLen = ApproxSplineLength(t.splineBuf, vineSplinePoints);
                t.growProgress = Mathf.Clamp01(
                    t.growProgress + tentacleGrowSpeed * dt / Mathf.Max(0.5f, arcLen));

                UpdateTentacleLine(t, starPos);

                if (t.growProgress >= 1f)
                {
                    t.tipPos           = t.targetWorldPos;
                    t.state            = TentacleState.Draining;
                    t.contactTimer     = 0f;
                    t.drainTimer       = drainTick; // first drain waits a full interval
                    t.ctrlRebuildTimer = 0f;
                }
                break;
            }

            case TentacleState.Draining:
            {
                if (!IsTargetValid(t.targetCell, t.role))
                {
                    t.state = TentacleState.Retracting;
                    break;
                }

                t.tipPos = t.targetWorldPos;

                t.drainFlashTimer = Mathf.Max(0f, t.drainFlashTimer - dt);

                // Keep vine alive and breathing while draining.
                t.ctrlRebuildTimer -= dt;
                if (t.ctrlRebuildTimer <= 0f)
                {
                    t.ctrlRebuildTimer = vineCtrlRebuildInterval;
                    RebuildBaseControlPts(t, starPos);
                }

                UpdateTentacleLine(t, starPos);

                // Minimum contact time gate — vine must be attached before energy chips.
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
                // Simple 2-point snap-back; no spline needed for an interrupted/invalid retract.
                t.tipPos = Vector2.MoveTowards(t.tipPos, starPos, tentacleRetractSpeed * dt);
                UpdateTentacleLine(t, starPos);

                if (Vector2.Distance(t.tipPos, starPos) < 0.05f)
                {
                    t.state        = TentacleState.Idle;
                    t.line.enabled = false;
                }
                break;
            }

            case TentacleState.Dissolving:
            {
                t.dissolveTimer   += dt;
                t.drainFlashTimer  = Mathf.Max(0f, t.drainFlashTimer - dt);
                t.alphaScale       = Mathf.Clamp01(1f - t.dissolveTimer / dissolveDuration);
                t.tipPos           = t.targetWorldPos;
                UpdateTentacleLine(t, starPos);

                if (t.dissolveTimer >= dissolveDuration)
                {
                    t.state                = TentacleState.Idle;
                    t.alphaScale           = 1f;
                    t.dissolveTimer        = 0f;
                    t.growProgress         = 0f;
                    t.line.enabled         = false;
                    t.line.widthMultiplier = tentacleWidth; // restore for next use
                }
                break;
            }
        }
    }

    // ---------------------------------------------------------------
    // Drain a single tick from the target cell
    // ---------------------------------------------------------------
    private void DrainTick(Tentacle t, CosmicDustGenerator gen)
    {
        if (gen == null) return;
        if (!gen.TryGetDustAt(t.targetCell, out var dust) || dust == null) return;

        int chipUnits   = Mathf.Max(1, Mathf.RoundToInt(drainRatePerSec * drainTick));
        int actualUnits = dust.ChipEnergy(chipUnits);

        if (actualUnits > 0 && _star != null)
        {
            _star.AddCharge(t.role, (float)actualUnits);
            onDelivery?.Invoke(t.role, (float)actualUnits);
            t.drainFlashTimer = drainFlashDuration; // brief tip sparkle on each chip
        }

        if (dust.currentEnergyUnits <= 0)
        {
            // Do not schedule regrow — the maze grows once and drained cells stay empty
            // until the Vehicle carves new paths through them (or the motif ends and resets).
            // Regrowing with Role.None would block Co_WaitForColoredDust indefinitely.
            gen.ClearCell(t.targetCell,
                CosmicDustGenerator.DustClearMode.FadeAndHide,
                fadeSeconds: 1.5f,
                scheduleRegrow: false);
            t.state         = TentacleState.Dissolving;
            t.dissolveTimer = 0f;
            t.alphaScale    = 1f;
        }
    }

    // ---------------------------------------------------------------
    // Vine spline — obstacle-pushed base rebuild (slow tick, ~0.15s)
    // ---------------------------------------------------------------
    private void RebuildBaseControlPts(Tentacle t, Vector2 starPos)
    {
        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        gen.GetColoredDustCells(_dustScratch);

        Vector2 path = t.targetWorldPos - starPos;
        float   len  = path.magnitude;
        Vector2 perp = len > 0.0001f
            ? new Vector2(-path.y, path.x) / len
            : Vector2.up;

        for (int i = 0; i < vineCtrlPointCount; i++)
        {
            float   baseT   = (i + 1f) / (vineCtrlPointCount + 1f);
            Vector2 basePos = starPos + path * baseT;

            // Slow Perlin shift that changes between rebuilds, giving the vine a wandering base shape.
            float seed     = (float)t.role * 31.4f + i * 7.3f;
            float noiseVal = Mathf.PerlinNoise(seed, Time.time * 0.15f) * 2f - 1f;
            // Arc envelope: offset peaks at midpoint and tapers to 0 at endpoints.
            float offset   = noiseVal * vineNoiseAmplitude * baseT * (1f - baseT) * 4f;

            t.baseControlPts[i] = basePos + perp * offset;
        }

        // Repulsion: push each control point away from nearby non-target colored dust.
        for (int ci = 0; ci < _dustScratch.Count; ci++)
        {
            Vector2Int cell = _dustScratch[ci];
            if (cell == t.targetCell) continue;

            Vector2 cellWorld = drum.GridToWorldPosition(cell);

            for (int pi = 0; pi < vineCtrlPointCount; pi++)
            {
                Vector2 delta = t.baseControlPts[pi] - cellWorld;
                float   dist  = delta.magnitude;
                if (dist < vineDustRepulsionRadius && dist > 0.001f)
                {
                    float strength        = (1f - dist / vineDustRepulsionRadius) * vineDustRepulsionStrength;
                    t.baseControlPts[pi] += (delta / dist) * strength;
                }
            }
        }
    }

    // ---------------------------------------------------------------
    // Vine spline — per-frame evaluation (fast breathe, fills splineBuf)
    // ---------------------------------------------------------------
    private void EvaluateVineSpline(Tentacle t, Vector2 starPos)
    {
        Vector2 path = t.targetWorldPos - starPos;
        float   len  = path.magnitude;
        Vector2 perp = len > 0.0001f
            ? new Vector2(-path.y, path.x) / len
            : Vector2.up;

        // Anchor points
        t.ctrlBuf[0]                    = starPos;
        t.ctrlBuf[vineCtrlPointCount + 1] = t.targetWorldPos;

        // Intermediate points: base position + subtle per-frame breathe
        for (int i = 0; i < vineCtrlPointCount; i++)
        {
            float seed       = (float)t.role * 31.4f + i * 7.3f;
            float breatheVal = Mathf.PerlinNoise(seed, Time.time / vineBreathePeriod) * 2f - 1f;
            t.ctrlBuf[i + 1] = t.baseControlPts[i] + perp * (breatheVal * vineNoiseAmplitude * 0.15f);
        }

        CatmullRomChain(t.ctrlBuf, vineCtrlPointCount + 2, t.splineBuf, vineSplinePoints);
    }

    // ---------------------------------------------------------------
    // Line visual
    // ---------------------------------------------------------------
    // Returns the world-space point where the line root should appear.
    // Uses a smoothed direction from the diamond center toward the target so the root
    // always faces the dust and arcs smoothly rather than flipping with the diamond spin.
    private Vector2 GetLineRoot(Tentacle t, Vector2 targetWorldPos)
    {
        var diamond = _star?.PrimaryDiamondTransform;
        if (diamond == null) return (Vector2)transform.position;

        var sr = diamond.GetComponent<SpriteRenderer>();
        float tipDist = (sr != null && sr.sprite != null)
            ? sr.sprite.bounds.extents.y
            : diamondTipOffset;

        // Anchor to the physical top of the spinning diamond sprite.
        // diamond.up reflects the localRotation set each frame by UpdateDualDiamonds,
        // so the root whips around with the diamond as it spins.
        return (Vector2)diamond.position + (Vector2)diamond.up * tipDist;
    }

    private void UpdateTentacleLine(Tentacle t, Vector2 starPos)
    {
        Vector2 lineRoot = GetLineRoot(t, t.targetWorldPos);

        // Retracting: simple 2-point gray snap-back (organic path not needed).
        if (t.state == TentacleState.Retracting)
        {
            t.line.positionCount   = 2;
            t.line.widthMultiplier = tentacleWidth;
            t.line.SetPosition(0, (Vector3)lineRoot);
            t.line.SetPosition(1, (Vector3)t.tipPos);
            BuildGradient(t, GetRoleColor(t.role));
            t.line.colorGradient = t.gradient;
            return;
        }

        // All other states: organic Catmull-Rom spline.
        // EvaluateVineSpline uses the true star center so control-point math is stable;
        // we replace only the first rendered position with the tip-offset root.
        EvaluateVineSpline(t, starPos);

        int renderCount = (t.state == TentacleState.Growing)
            ? Mathf.Max(2, Mathf.RoundToInt(t.growProgress * (vineSplinePoints - 1)) + 1)
            : vineSplinePoints; // Draining or Dissolving: full spline

        t.line.positionCount   = renderCount;
        t.line.widthMultiplier = tentacleWidth * t.alphaScale; // Dissolving shrinks the line

        t.line.SetPosition(0, (Vector3)lineRoot); // override root to diamond tip
        for (int i = 1; i < renderCount; i++)
            t.line.SetPosition(i, (Vector3)t.splineBuf[i]);

        // Sample dust charge so the tip taper matches the drain state.
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
            // Phase 1: color front sweeps from tip (pos 1) toward root (pos 0).
            float colorFill  = Mathf.Clamp01(t.contactTimer / Mathf.Max(0.001f, colorTransitionTime));
            float colorFront = 1f - colorFill; // 1.0 at arrival, 0.0 when fully colored

            if (colorFill < 0.999f)
            {
                float edge0 = Mathf.Max(0f, colorFront - 0.08f);
                float edge1 = Mathf.Min(1f, colorFront + 0.08f);
                // Tip fades to match the dust's current drain color, tapers to transparent.
                Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);

                t.gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(Color.gray,    0f),
                        new GradientColorKey(Color.gray,    edge0),
                        new GradientColorKey(roleColor,     edge1),
                        new GradientColorKey(dustTipColor,  0.92f),
                        new GradientColorKey(dustTipColor,  1f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(0.45f * a, 0f),
                        new GradientAlphaKey(0.45f * a, edge0),
                        new GradientAlphaKey(0.85f * a, edge1),
                        new GradientAlphaKey(dustCharge01 * 0.5f * a, 0.92f),
                        new GradientAlphaKey(0f, 1f),   // fade to transparent at dust end
                    }
                );
                return;
            }

            // Phase 2: fully colored — animated pulse flowing from tip to root.
            BuildDrainPulseGradient(t, roleColor, a, dustCharge01);
        }
        else if (t.state == TentacleState.Dissolving)
        {
            // Dissolving always starts fully colored.
            BuildDrainPulseGradient(t, roleColor, a, dustCharge01);
        }
        else
        {
            // Growing / Retracting: neutral gray probe.
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
        float rawT  = (Time.time * tentacleFlowSpeed + t.flowOffset) % 1f;
        float pulse = 1f - rawT;
        float hw    = 0.1f;
        float p0    = Mathf.Clamp01(pulse - hw);
        float p1    = pulse;
        float p2    = Mathf.Clamp01(pulse + hw);

        float flashBoost = t.drainFlashTimer > 0f ? 0.5f : 0.4f;
        Color bright = Color.Lerp(roleColor, Color.white, flashBoost);

        // Tip color matches the dust's current drain state; fades to transparent at the dust end.
        Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);
        float tipAlpha     = dustCharge01 * 0.6f * a;

        t.gradient.SetKeys(
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
                new GradientAlphaKey(0.35f * a, 0f),
                new GradientAlphaKey(0.2f  * a, p0),
                new GradientAlphaKey(1.0f  * a, p1),
                new GradientAlphaKey(0.2f  * a, p2),
                new GradientAlphaKey(tipAlpha,  0.92f),
                new GradientAlphaKey(0f,        1f),   // fade to transparent at dust end
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

    // ---------------------------------------------------------------
    // Catmull-Rom spline math (inlined — no external dependency)
    // ---------------------------------------------------------------

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    /// <summary>
    /// Evaluates a Catmull-Rom chain into <paramref name="outPts"/> (exactly <paramref name="outCount"/> samples).
    /// Ghost endpoints are reflected inward so the curve passes cleanly through ctrl[0] and ctrl[ctrlCount-1].
    /// </summary>
    private static void CatmullRomChain(Vector2[] ctrl, int ctrlCount, Vector2[] outPts, int outCount)
    {
        int spans = ctrlCount - 1;
        for (int i = 0; i < outCount; i++)
        {
            float globalT = (outCount > 1) ? (float)i / (outCount - 1) : 0f;
            float spanF   = globalT * spans;
            int   spanIdx = Mathf.Min((int)spanF, spans - 1);
            float localT  = spanF - spanIdx;

            Vector2 p0 = (spanIdx == 0)
                ? 2f * ctrl[0] - ctrl[1]
                : ctrl[spanIdx - 1];
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

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private bool IsTargetValid(Vector2Int cell, MusicalRole role)
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null) return false;
        if (!gen.HasDustAt(cell)) return false;
        if (!gen.TryGetDustAt(cell, out var dust) || dust == null) return false;
        return dust.Role == role && dust.currentEnergyUnits > 0;
    }

    private LineRenderer CreateTentacleLine(MusicalRole role)
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
        lr.material           = tentacleMaterial != null
            ? tentacleMaterial
            : new Material(Shader.Find("Sprites/Default"));
        lr.SetPosition(0, transform.position);
        lr.SetPosition(1, transform.position);
        lr.enabled = false;
        return lr;
    }

    // ---------------------------------------------------------------
    // Keep-clear pocket
    // ---------------------------------------------------------------
    private void TickKeepClear()
    {
        if (_profile == null || !_profile.starKeepsDustClear) return;

        var gfm   = GameFlowManager.Instance;
        var gen   = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;
        if (gen == null || drums == null) return;

        Vector2Int center = drums.WorldToGridPosition(transform.position);
        gen.SetStarKeepClear(center, _profile.starKeepClearRadiusCells, forceRemoveExisting: false);
    }
}
