using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Handles the PhaseStar's interaction with the cosmic dust field.
///
/// Two systems run in parallel:
///
/// 1) Scout state machine:
///    The scout diamond travels from the star to a target dust cell, drains
///    25% of that cell's energy, then returns and "docks" back at the star.
///    While docked the scout color bleeds into the accumulator.
///    States: Idle → Launching → Draining → Returning → Docked → (repeat)
///
/// 2) Proximity drain (TickProximityDrain):
///    While the scout is Idle or Docked, the existing probe logic drains cells
///    that are physically adjacent to the star as it moves into them. This
///    handles the bulk drain after the star reaches the target cell.
///    Disabled during Launching/Returning to prevent double-drain.
///
/// 3) Keep-clear pocket (optional, profile-gated):
///    Claims a small radius around the star so regrowth does not fill in
///    the immediate pocket.
/// </summary>
[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    // ---------------------------------------------------------------
    // Inspector — Keep-Clear
    // ---------------------------------------------------------------
    [Header("Keep-Clear Pocket (optional)")]
    [SerializeField] private float keepClearTick = 0.15f;

    // ---------------------------------------------------------------
    // Inspector — Proximity Drain (bulk drain when star is at cell)
    // ---------------------------------------------------------------
    [Header("Proximity Drain")]
    [Tooltip("How often the proximity probe fires (seconds).")]
    [SerializeField] private float drainTick = 0.08f;

    [Tooltip("Energy units drained per second from each contacted dust cell.")]
    [SerializeField] private float drainRatePerSec = 1.8f;

    [Tooltip("How many cells ahead of travel direction to probe. 1 = immediate neighbour only.")]
    [SerializeField, Range(1, 4)] private int probeDepthCells = 2;

    // ---------------------------------------------------------------
    // Inspector — Scout
    // ---------------------------------------------------------------
    [Header("Scout Diamond")]
    [Tooltip("Child transform holding the scout SpriteRenderer. Created at runtime if null.")]
    [SerializeField] private Transform scoutVisualRoot;

    [Tooltip("Sprite for the scout diamond. Falls back to first SpriteRenderer on the star.")]
    [SerializeField] private Sprite scoutSprite;

    [Tooltip("World units per second during scout flight.")]
    [SerializeField] private float scoutTravelSpeed = 6f;

    [Tooltip("Fraction of maxEnergyUnits drained on each scout trip (0 = none, 1 = full).")]
    [SerializeField, Range(0f, 1f)] private float scoutDrainFraction = 0.25f;

    [Tooltip("Oscillation speed (cycles/sec) for the idle/docked pulse.")]
    [SerializeField] private float scoutPulseSpeed = 0.6f;

    [Tooltip("Minimum scale for the pulse and during flight.")]
    [SerializeField] private float scoutPulseMinScale = 0.3f;

    [Tooltip("Rotation speed of the scout diamond (deg/sec). Accumulator rotates at the opposite sign.")]
    [SerializeField] private float scoutRotSpeed = 90f;

    [Tooltip("Normal (docked/idle) alpha of the scout.")]
    [SerializeField, Range(0f, 1f)] private float scoutIdleAlpha = 0.55f;

    [Tooltip("Max angular tilt (degrees) applied as the scout wobbles left-right during travel.")]
    [SerializeField] private float scoutWobbleAmplitudeDeg = 18f;

    [Tooltip("Cell-size legs between sniff pauses during patrol flight.")]
    [SerializeField] private float scoutPatrolCellsPerPause = 2.5f;

    [Tooltip("Duration of each sniff hover (seconds).")]
    [SerializeField] private float scoutPatrolSniffSeconds = 0.55f;

    [Tooltip("Spin speed multiplier while sniffing.")]
    [SerializeField] private float scoutPatrolSniffSpinMul = 3.5f;

    // Regrow delay after a proximity drain fully empties a cell.
    [SerializeField] private float starDrainRegrowDelay = 6f;

    // ---------------------------------------------------------------
    // Scout state machine
    // ---------------------------------------------------------------
    private enum ScoutState { Idle, Launching, Draining, Returning, Docked }

    private bool       _scoutActive = false;
    private ScoutState _scoutState  = ScoutState.Idle;
    private Vector2    _scoutTargetWorld;
    private Vector2Int _scoutTargetCell;
    private float      _scoutRotAngle;
    private float      _scoutPulseTimer;
    private Color      _scoutCurrentColor = Color.gray;
    private float      _patrolDistAccum;   // world units traveled in current patrol leg
    private float      _patrolSniffTimer;  // >0 = hovering/sniffing

    // Charge delivery held until the scout docks so the PreviewShard only reacts on return.
    private MusicalRole _pendingChargeRole;
    private float       _pendingChargeFraction;

    // Shared pulse t01 exposed to PhaseStar so the accumulator can sync.
    private float _sharedPulseT;

    // ---------------------------------------------------------------
    // Fired when non-zero units are delivered (scout docks back).
    // Payload: (role, fractionDelivered). PhaseStar subscribes.
    // ---------------------------------------------------------------
    public System.Action<MusicalRole, float> onDelivery;

    // ---------------------------------------------------------------
    // Internal state
    // ---------------------------------------------------------------
    private float _keepClearTimer;
    private float _drainTimer;

    private PhaseStarBehaviorProfile _profile;
    private PhaseStar                _star;
    private PhaseStarCravingNavigator _navigator;
    private Rigidbody2D              _rb;
    private Collider2D[]             _myColliders;

    private Vector2 _lastPos;
    private SpriteRenderer _scoutRenderer;

    private readonly Vector2Int[] _drainProbeBuffer = new Vector2Int[16];

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile   = profile;
        _star      = star;
        _navigator = GetComponent<PhaseStarCravingNavigator>();
        _rb        = GetComponent<Rigidbody2D>();
        _lastPos   = transform.position;
        _myColliders = GetComponentsInChildren<Collider2D>(true);

        EnsureScoutVisual();

        star.OnArmed    += _ => { };   // hook point for future use
        star.OnDisarmed += _ =>
        {
            var gfm = GameFlowManager.Instance;
            if (gfm?.activeDrumTrack != null)
                gfm.activeDrumTrack.isPhaseStarActive = false;

            var gen = gfm?.dustGenerator;
            if (gen != null)
                gen.ClearStarKeepClear();
        };
    }

    /// <summary>
    /// Resets the scout to Idle/gray/docked. Called by PhaseStar immediately on eject.
    /// </summary>
    public void ResetScout()
    {
        _scoutState        = ScoutState.Idle;
        _scoutCurrentColor = Color.gray;
        _scoutRotAngle     = 90f;   // offset 90° from accumulator (which starts at 0°)
        if (scoutVisualRoot != null)
            scoutVisualRoot.localPosition = Vector3.zero;
        if (_scoutRenderer != null)
        {
            var c = Color.gray; c.a = 0f;
            _scoutRenderer.color   = c;
            _scoutRenderer.enabled = false;
        }
    }

    /// <summary>
    /// Enables or disables the scout. When false the scout is invisible and its
    /// state machine is frozen. Called by PhaseStar on disarm (false) and arm (true).
    /// </summary>
    public void SetScoutActive(bool active)
    {
        _scoutActive = active;
        if (!active && _scoutRenderer != null)
            _scoutRenderer.enabled = false;
    }

    /// <summary>Current role color the scout is carrying (or gray when idle).</summary>
    public Color GetDockedColor() => _scoutCurrentColor;

    /// <summary>Current pulse t01 (0–1) for accumulator sync.</summary>
    public float GetSharedPulseT() => _sharedPulseT;

    /// <summary>Current world position of the scout visual (star center when not in flight).</summary>
    public Vector2 GetScoutWorldPos() =>
        scoutVisualRoot != null ? (Vector2)scoutVisualRoot.position : (Vector2)transform.position;

    /// <summary>True while the scout is physically traveling (outbound or return).</summary>
    public bool IsScoutInFlight =>
        _scoutActive && (_scoutState == ScoutState.Launching || _scoutState == ScoutState.Returning);

    /// <summary>True when the star is docked and physically adjacent to the target cell (proximity drain active).</summary>
    public bool IsAtTarget =>
        _scoutActive && _scoutState == ScoutState.Docked && IsStarAdjacentToTarget();

    private bool IsStarAdjacentToTarget()
    {
        var drums = GameFlowManager.Instance?.activeDrumTrack;
        if (drums == null) return false;
        float threshold = Mathf.Max(0.001f, drums.GetCellWorldSize()) * 1.5f;
        return Vector2.Distance(transform.position, drums.GridToWorldPosition(_scoutTargetCell)) <= threshold;
    }

    /// <summary>No-op — dust colliders are no longer ignored.</summary>
    public void RefreshDustIgnore() { }

    // ---------------------------------------------------------------
    // Unity
    // ---------------------------------------------------------------
    private void Update()
    {
        float dt = Time.deltaTime;

        _keepClearTimer += dt;
        if (_keepClearTimer >= keepClearTick)
        {
            _keepClearTimer = 0f;
            TickKeepClear();
        }

        TickScout(dt);

        // Proximity drain when active. Runs during Idle, Docked, and Launching so the star
        // can carve through blocking dust cells while physically traveling to its target.
        if (_scoutActive && _scoutState != ScoutState.Returning)
        {
            _drainTimer += dt;
            if (_drainTimer >= drainTick)
            {
                _drainTimer = 0f;
                TickProximityDrain();
            }
        }

        _lastPos = transform.position;
    }

    // ---------------------------------------------------------------
    // Scout state machine
    // ---------------------------------------------------------------
    private void TickScout(float dt)
    {
        if (!_scoutActive) return;

        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;

        // Advance pulse timer always (keeps the pulse continuous).
        _scoutPulseTimer += dt;
        _sharedPulseT = (Mathf.Sin(_scoutPulseTimer * scoutPulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;

        switch (_scoutState)
        {
            case ScoutState.Idle:
                UpdateScoutAtStar();
                // Try to launch if navigator has a valid, role-colored target.
                if (_navigator != null && _navigator.HasTarget && gen != null)
                {
                    Vector2Int cell = _navigator.GetTargetCell();
                    if (gen.TryGetDustAt(cell, out var d) && d != null && d.Role != MusicalRole.None && d.currentEnergyUnits > 0)
                    {
                        _scoutTargetCell   = cell;
                        _scoutTargetWorld  = _navigator.GetTargetWorldPos();
                        _scoutCurrentColor = Color.gray;
                        if (_scoutRenderer != null)
                        {
                            var c = Color.gray; c.a = scoutIdleAlpha;
                            _scoutRenderer.color = c;
                        }
                        _patrolDistAccum  = 0f;
                        _patrolSniffTimer = 0f;
                        _scoutState = ScoutState.Launching;
                    }
                }
                break;

            case ScoutState.Launching:
            {
                // Keep target world pos fresh if navigator updates.
                if (_navigator != null && _navigator.HasTarget && _navigator.GetTargetCell() == _scoutTargetCell)
                    _scoutTargetWorld = _navigator.GetTargetWorldPos();

                Vector2 current = scoutVisualRoot != null
                    ? (Vector2)scoutVisualRoot.position : (Vector2)transform.position;

                if (_patrolSniffTimer > 0f)
                {
                    // Sniff phase: hover in place, spin fast to signal "thinking".
                    _patrolSniffTimer -= dt;
                    _scoutRotAngle += scoutRotSpeed * scoutPatrolSniffSpinMul * dt;
                    if (scoutVisualRoot != null)
                        scoutVisualRoot.localRotation = Quaternion.Euler(0f, 0f, _scoutRotAngle);
                    UpdateScoutColor(_scoutCurrentColor, scoutIdleAlpha);
                }
                else
                {
                    // Move phase: advance toward target, accumulate distance traveled.
                    float cellSize = GameFlowManager.Instance?.activeDrumTrack != null
                        ? GameFlowManager.Instance.activeDrumTrack.GetCellWorldSize() : 1f;
                    float legLength = scoutPatrolCellsPerPause * Mathf.Max(0.1f, cellSize);

                    Vector2 next = Vector2.MoveTowards(current, _scoutTargetWorld, scoutTravelSpeed * dt);
                    _patrolDistAccum += Vector2.Distance(current, next);

                    if (scoutVisualRoot != null)
                        scoutVisualRoot.position = new Vector3(next.x, next.y, scoutVisualRoot.position.z);

                    ApplyScoutMinScale();
                    Vector2 dir = (_scoutTargetWorld - next).sqrMagnitude > 0.0001f
                        ? (_scoutTargetWorld - next).normalized
                        : (_scoutTargetWorld - (Vector2)transform.position).normalized;
                    ApplyTravelRotation(dir, dt);
                    UpdateScoutColor(_scoutCurrentColor, scoutIdleAlpha);

                    if (_patrolDistAccum >= legLength)
                    {
                        _patrolDistAccum  = 0f;
                        _patrolSniffTimer = scoutPatrolSniffSeconds;
                    }
                }

                // Arrival check — works whether sniffing or moving.
                Vector2 scoutPos = scoutVisualRoot != null
                    ? (Vector2)scoutVisualRoot.position : (Vector2)transform.position;
                if (Vector2.Distance(scoutPos, _scoutTargetWorld) < 0.05f)
                    _scoutState = ScoutState.Draining;
                break;
            }

            case ScoutState.Draining:
            {
                // Sample-only trip: identify the role and latch its color.
                // Do NOT chip energy — the dust is untouched here.
                // The star's proximity drain handles actual consumption and cell removal
                // once the star physically reaches the cell.
                if (gen != null && gen.TryGetDustAt(_scoutTargetCell, out var dust) && dust != null
                    && dust.Role != MusicalRole.None && dust.currentEnergyUnits > 0)
                {
                    MusicalRole drainRole = dust.Role;

                    var roleProfile = MusicalRoleProfileLibrary.GetProfile(drainRole);
                    _scoutCurrentColor = roleProfile != null ? roleProfile.GetBaseColor() : Color.white;

                    // Deliver scoutDrainFraction of maxEnergyUnits so the PreviewShard
                    // alpha = scoutDrainFraction (e.g. 25%) after the scout returns.
                    _pendingChargeRole     = drainRole;
                    _pendingChargeFraction = scoutDrainFraction * Mathf.Max(1, dust.maxEnergyUnits);

                    _navigator?.NotifyDraining(_scoutTargetCell);
                }

                _scoutState = ScoutState.Returning;
                break;
            }

            case ScoutState.Returning:
            {
                Vector2 starPos = transform.position;
                Vector2 current = scoutVisualRoot != null ? (Vector2)scoutVisualRoot.position : starPos;
                Vector2 next    = Vector2.MoveTowards(current, starPos, scoutTravelSpeed * dt);

                if (scoutVisualRoot != null)
                    scoutVisualRoot.position = new Vector3(next.x, next.y, scoutVisualRoot.position.z);

                // Pulse alpha while carrying sample — never fully opaque (PreviewShard signals that).
                float returnAlpha = Mathf.Lerp(0.15f, 0.65f, _sharedPulseT);
                UpdateScoutColor(_scoutCurrentColor, returnAlpha);
                ApplyScoutMinScale();
                // Point nose toward star; wobble driven by spin motor.
                {
                    Vector2 dir = (starPos - next).sqrMagnitude > 0.0001f
                        ? (starPos - next).normalized
                        : Vector2.up;
                    ApplyTravelRotation(dir, dt);
                }

                if (Vector2.Distance(next, starPos) < 0.05f)
                {
                    // Snap back to local origin.
                    if (scoutVisualRoot != null)
                        scoutVisualRoot.localPosition = Vector3.zero;

                    // Deliver the pending charge now that the scout is home.
                    if (_pendingChargeFraction > 0f)
                    {
                        _star?.AddCharge(_pendingChargeRole, _pendingChargeFraction);
                        onDelivery?.Invoke(_pendingChargeRole, _pendingChargeFraction);
                        _pendingChargeFraction = 0f;
                    }

                    // Scout turns gray immediately on dock — sample delivered to accumulator.
                    _scoutCurrentColor = Color.gray;
                    if (_scoutRenderer != null)
                    {
                        var sc = Color.gray; sc.a = scoutIdleAlpha;
                        _scoutRenderer.color = sc;
                    }
                    _scoutState = ScoutState.Docked;
                }
                break;
            }

            case ScoutState.Docked:
            {
                // No pulse — lerp scale to full size and free-spin.
                if (scoutVisualRoot != null)
                {
                    scoutVisualRoot.localPosition = Vector3.zero;
                    scoutVisualRoot.localScale = Vector3.Lerp(
                        scoutVisualRoot.localScale, Vector3.one, Time.deltaTime * 6f);
                }
                RotateScout(Time.deltaTime);
                UpdateScoutColor(_scoutCurrentColor, scoutIdleAlpha);

                if (gen == null) break;

                // Keep the navigator committed to this cell while we're docked.
                _navigator?.NotifyDraining(_scoutTargetCell);

                // Only relaunch once the target cell is fully drained.
                bool targetGone = !gen.HasDustAt(_scoutTargetCell);

                if (targetGone)
                {
                    _scoutCurrentColor = Color.gray;
                    _navigator?.ClearLockOn(_scoutTargetCell);

                    // Check if there's already a new target ready.
                    if (_navigator != null && _navigator.HasTarget)
                    {
                        Vector2Int cell = _navigator.GetTargetCell();
                        if (gen.TryGetDustAt(cell, out var d) && d != null && d.Role != MusicalRole.None && d.currentEnergyUnits > 0)
                        {
                            _scoutTargetCell  = cell;
                            _scoutTargetWorld = _navigator.GetTargetWorldPos();
                            _scoutCurrentColor = Color.gray;
                            _patrolDistAccum  = 0f;
                            _patrolSniffTimer = 0f;
                            _scoutState        = ScoutState.Launching;
                            break;
                        }
                    }
                    _scoutState = ScoutState.Idle;
                }
                break;
            }
        }

        if (_scoutRenderer != null)
            _scoutRenderer.enabled = _scoutActive;
    }

    // ---------------------------------------------------------------
    // Scout visual helpers
    // ---------------------------------------------------------------

    private void UpdateScoutAtStar()
    {
        if (scoutVisualRoot != null)
            scoutVisualRoot.localPosition = Vector3.zero;
        ApplyScoutPulseScale();
        RotateScout(Time.deltaTime); // deltaTime already consumed by caller but negligible here
        UpdateScoutColor(_scoutCurrentColor, scoutIdleAlpha);
    }

    private void ApplyScoutPulseScale()
    {
        float s = Mathf.Lerp(scoutPulseMinScale, 1f, _sharedPulseT);
        if (scoutVisualRoot != null)
            scoutVisualRoot.localScale = Vector3.one * s;
    }

    private void ApplyScoutMinScale()
    {
        if (scoutVisualRoot != null)
            scoutVisualRoot.localScale = Vector3.one * scoutPulseMinScale;
    }

    private void RotateScout(float dt)
    {
        // Slow to 15% speed when docked at the drain target (eating animation).
        float mul = (_scoutState == ScoutState.Docked && IsStarAdjacentToTarget()) ? 0.15f : 1f;
        _scoutRotAngle += scoutRotSpeed * dt * mul;
        if (scoutVisualRoot != null)
            scoutVisualRoot.localRotation = Quaternion.Euler(0f, 0f, _scoutRotAngle);
    }

    /// <summary>
    /// During travel: accumulates the spin angle as the "motor" and applies it as a
    /// directional wobble — the diamond's nose points toward <paramref name="travelDir"/>
    /// while swaying left-right by <see cref="scoutWobbleAmplitudeDeg"/>.
    /// </summary>
    private void ApplyTravelRotation(Vector2 travelDir, float dt)
    {
        _scoutRotAngle += scoutRotSpeed * dt;
        float facingDeg = Mathf.Atan2(travelDir.y, travelDir.x) * Mathf.Rad2Deg;
        float wobble    = Mathf.Sin(_scoutRotAngle * Mathf.Deg2Rad) * scoutWobbleAmplitudeDeg;
        if (scoutVisualRoot != null)
            scoutVisualRoot.localRotation = Quaternion.Euler(0f, 0f, facingDeg + wobble);
    }

    private void UpdateScoutColor(Color targetColor, float alpha)
    {
        if (_scoutRenderer == null) return;
        targetColor.a = alpha;
        _scoutRenderer.color = Color.Lerp(_scoutRenderer.color, targetColor, Time.deltaTime * 6f);
    }

    // ---------------------------------------------------------------
    // Proximity drain (bulk drain when star is physically at the cell)
    // ---------------------------------------------------------------
    private void TickProximityDrain()
    {
        var gfm   = GameFlowManager.Instance;
        var gen   = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;
        if (gen == null || drums == null) return;

        // When Docked, only drain cells once the star is physically adjacent to the committed
        // target so it doesn't strip dust at range. During Idle and Launching the star drains
        // whatever it is physically touching — this lets it carve a path through blocking cells.
        if (_scoutState == ScoutState.Docked)
        {
            float adjacencyThreshold = Mathf.Max(0.001f, drums.GetCellWorldSize()) * 1.5f;
            Vector2 tgtWorld         = drums.GridToWorldPosition(_scoutTargetCell);
            if (Vector2.Distance(transform.position, tgtWorld) > adjacencyThreshold)
                return;
        }

        Vector2 pos = transform.position;

        Vector2 travelDir = Vector2.zero;
        if (_rb != null && _rb.linearVelocity.sqrMagnitude > 0.01f)
            travelDir = _rb.linearVelocity.normalized;
        else
        {
            Vector2 delta = pos - _lastPos;
            if (delta.sqrMagnitude > 0.0001f) travelDir = delta.normalized;
        }

        float cellSize    = Mathf.Max(0.001f, drums.GetCellWorldSize());
        float drainAmount = drainRatePerSec * drainTick;

        Vector2Int baseCell = drums.WorldToGridPosition(pos);
        int probeCount = 0;
        _drainProbeBuffer[probeCount++] = baseCell;
        _drainProbeBuffer[probeCount++] = baseCell + new Vector2Int( 1,  0);
        _drainProbeBuffer[probeCount++] = baseCell + new Vector2Int(-1,  0);
        _drainProbeBuffer[probeCount++] = baseCell + new Vector2Int( 0,  1);
        _drainProbeBuffer[probeCount++] = baseCell + new Vector2Int( 0, -1);
        if (travelDir.sqrMagnitude > 0.001f)
        {
            for (int depth = 2; depth <= probeDepthCells; depth++)
            {
                Vector2 probeWorld = pos + travelDir * (cellSize * depth);
                _drainProbeBuffer[probeCount++] = drums.WorldToGridPosition(probeWorld);
            }
        }

        int chipUnits = Mathf.Max(1, Mathf.RoundToInt(drainAmount));
        bool lockedThisTick = false;

        for (int pi = 0; pi < probeCount; pi++)
        {
            Vector2Int probeCell = _drainProbeBuffer[pi];
            if (!gen.HasDustAt(probeCell)) continue;
            if (!gen.TryGetCellGo(probeCell, out var go) || go == null) continue;
            if (!go.TryGetComponent<CosmicDust>(out var dustCell) || dustCell == null) continue;

            MusicalRole drainRole = dustCell.Role;
            if (drainRole == MusicalRole.None) continue;

            // Cell may have been pre-drained to 0 by the scout — clear it and move on.
            if (dustCell.currentEnergyUnits <= 0)
            {
                _navigator?.ClearLockOn(probeCell);
                gen.ClearImprintAt(probeCell);
                gen.ClearCell(probeCell, CosmicDustGenerator.DustClearMode.FadeAndHide,
                    fadeSeconds: 0.4f, scheduleRegrow: true, regrowDelaySeconds: starDrainRegrowDelay);
                continue;
            }

            int actualUnits = dustCell.ChipEnergy(chipUnits);
            if (actualUnits > 0 && _star != null)
            {
                float taken = (float)actualUnits / Mathf.Max(1, dustCell.maxEnergyUnits);
                // Only credit charge for the role the star actually needs.
                // Wrong-role cells are still physically drained to clear the path.
                MusicalRole needed = _star.GetPreviewRole();
                if (needed == MusicalRole.None || drainRole == needed)
                {
                    _star.AddCharge(drainRole, taken);
                    onDelivery?.Invoke(drainRole, taken);
                }

                if (!lockedThisTick && drainRole == needed)
                {
                    _navigator?.NotifyDraining(probeCell);
                    lockedThisTick = true;
                }
            }

            if (dustCell.currentEnergyUnits <= 0)
            {
                _navigator?.ClearLockOn(probeCell);
                gen.ClearImprintAt(probeCell);
                gen.ClearCell(probeCell, CosmicDustGenerator.DustClearMode.FadeAndHide,
                    fadeSeconds: 0.4f,
                    scheduleRegrow: true,
                    regrowDelaySeconds: starDrainRegrowDelay);
            }
        }
    }

    // ---------------------------------------------------------------
    // Keep-clear
    // ---------------------------------------------------------------
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

    // ---------------------------------------------------------------
    // Scout visual setup
    // ---------------------------------------------------------------
    private void EnsureScoutVisual()
    {
        if (scoutVisualRoot == null)
        {
            var go = new GameObject("Scout Visual");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            scoutVisualRoot = go.transform;
        }

        if (_scoutRenderer == null)
            _scoutRenderer = scoutVisualRoot.GetComponent<SpriteRenderer>();
        if (_scoutRenderer == null)
            _scoutRenderer = scoutVisualRoot.gameObject.AddComponent<SpriteRenderer>();

        if (_scoutRenderer.sprite == null)
        {
            if (scoutSprite != null)
            {
                _scoutRenderer.sprite = scoutSprite;
            }
            else
            {
                // Fall back to the diamond sprite used by the accumulator (PhaseStarVisuals2D
                // is on the same GameObject as this component).
                var vis = GetComponent<PhaseStarVisuals2D>();
                if (vis != null && vis.diamond != null)
                    _scoutRenderer.sprite = vis.diamond;
            }
        }

        scoutVisualRoot.localScale = Vector3.one * scoutPulseMinScale;
        _scoutRotAngle = 90f;   // start offset 90° from the accumulator
        scoutVisualRoot.localRotation = Quaternion.Euler(0f, 0f, _scoutRotAngle);
        var c = Color.gray; c.a = 0f;
        _scoutRenderer.color   = c;
        _scoutRenderer.enabled = false;
    }
}
