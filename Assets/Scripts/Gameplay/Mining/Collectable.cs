using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class Collectable : MonoBehaviour
{
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public SpriteRenderer energySprite;
    public int burstId;
    [Header("Spawn Intent")]
    public bool isTrappedInDust = false;
    // ---- Dust collision tuning ----
    public float dustCollisionEnterImpulse = 0.85f;
    public float dustCollisionStayForce = 2.25f;
    public float dustEscapeCooldownSeconds = 0.35f;
    private float _dustEscapeCooldown;
    private bool _hasDesiredGridTarget; 
    public int intendedBin = -1;
    private Vector2Int _desiredGridTarget;
// ---- Dust pocket (collectable visibility) ----
    [Header("Dust Pocket (Visibility)")]
    [SerializeField] private bool keepDustPocketOpen = true;

// World radius of the pocket around the collectable (tune per phase later if desired)
    [SerializeField] private float dustPocketRadiusWorld = 0.55f;

// How often we refresh the pocket hold (seconds)
    [SerializeField] private float dustPocketTickSeconds = 0.15f;

// How long each refresh holds regrowth back (seconds)
// (Must be >= dustPocketTickSeconds to be effective.)
    [SerializeField] private float dustPocketHoldSeconds = 0.45f;
// ---- Deposit timing knobs ----
    [Header("Deposit Timing")]
    [Tooltip("How long the tether travel should take under normal circumstances (seconds).")]
    [SerializeField] private float depositTravelSeconds = 1.6f;   // readable default

    [Tooltip("Hard minimum travel time, unless the deposit moment is sooner than this.")]
    [SerializeField] private float minDepositTravelSeconds = 1.0f;

    [Tooltip("Hard maximum travel time (prevents overly slow zips).")]
    [SerializeField] private float maxDepositTravelSeconds = 2.2f;

    [Tooltip("Maximum readable orbit time before launch (seconds).")]
    [SerializeField] private float maxOrbitSeconds = 1.10f;

    [Tooltip("If the deposit moment is this soon, skip orbit and just travel (seconds).")]
    [SerializeField] private float imminentNoOrbitThreshold = 0.28f;

    [Tooltip("Small safety margin in DSP scheduling (seconds).")]
    [SerializeField] private float dspEpsilon = 0.010f;

    [Tooltip("Minimum lead time so travel doesn't start 'late' due to frame jitter.")]
    [SerializeField] private float carryMinLeadSeconds = 0.02f;
    private Coroutine _dustPocketRoutine;
    private int amount = 1;
    private int noteDurationTicks = 4; // 🎵 Default to 1/16th note duration
    private int assignedNote;          // 🎵 The MIDI note value
    public Transform ribbonMarker;           // assigned when spawned
    public NoteTether tether;               // runtime
    private bool awaitingPulse = false;
    public int intendedStep = -1;       // set at spawn (authoritative target)

    private bool isInitialized = false;
    private bool reachedDestination = false;
// Carry-as-child (school run) tuning
    [SerializeField] private Vector3 carryLocalOffset = new Vector3(0f, -0.65f, 0f);
    [SerializeField] private float carryLocalOffsetJitter = 0.05f; // optional tiny wobble
    private Transform _carryParent;
    // Idempotency flags
    private bool _handled;            // prevents double processing on trigger
    private bool _destroyNotified;    // prevents double OnDestroyed

    // Optional availability tension (currently only used if you enable it)
    private double _availableUntilDsp;
    [SerializeField] [Range(0.6f, 1.4f)]
    private float availabilityFactor = 1.0f;
    [SerializeField] private float pulseSpeed = 1.6f;
    [SerializeField] private float minAlpha = 0.25f;
    [SerializeField] private float maxAlpha = 0.65f;
    [SerializeField] private float pulseScale = 1.08f;
    private Coroutine _pulseRoutine;
    // Collectable.cs (top-level in class)
    static readonly Dictionary<Vector2Int, Collectable> _occupantByCell = new();
    static readonly Dictionary<Vector2Int, Collectable> _reservedByCell = new();
    static readonly object _lock = new object(); // optional; Unity main thread makes this mostly unnecessary
    private int _dustClaimOwnerId;
    Vector2Int _currentCell;
    Vector2Int _reservedCell;
    // ---- Autonomy (Loop Boundary "Idea") ----
    [Header("Autonomy (Loop Boundary Idea)")]
    [SerializeField] private bool useLoopBoundaryIdea = true;

    [Tooltip("How many grid cells ahead we evaluate when choosing an idea direction.")]
    [SerializeField] private int ideaLookaheadCells = 5;

    [Tooltip("Idea direction bias strength as a fraction of base move speed.")]
    [Range(0f, 1.5f)]
    [SerializeField] private float ideaBiasStrength = 0.55f;

    [Tooltip("How quickly the note turns toward its new idea direction.")]
    [SerializeField] private float ideaTurnLerp = 6.0f;

    [Tooltip("Small turbulence layered on top of idea bias.")]
    [Range(0f, 1f)]
    [SerializeField] private float microTurbulenceStrength = 0.20f;

    private Vector2 _ideaDir = Vector2.zero;
    private Vector2 _ideaDirSmoothed = Vector2.zero;
    private DrumTrack _boundDrumTrack;

// ---- Carry Orbit ----
    [Header("Carry Orbit")]
    [SerializeField] private float carryOrbitRadius = 0.55f;
    [SerializeField] private float carryOrbitAngularSpeed = 3.0f; // radians/sec
    [SerializeField] private float carryOrbitFollowLerp = 18f;
    [SerializeField] private float carryOrbitVerticalBias = 0.05f; // small upward bias so it reads above vehicle

    private int _carryOrbitIndex = -1;
    private float _carryOrbitPhaseOffset;
    private bool _registeredInCarryOrbit;

// Vehicle transform -> carried collectables (for spacing)
    private static readonly Dictionary<Transform, List<Collectable>> _carryOrbitByCollector = new();    
    private Transform _collector;
    private Coroutine _carryRoutine;
    private bool _inCarry;
    bool _hasReservation;
    public bool ReportedCollected { get; private set; }
    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;   // informational; does not call the track
    public event Action OnDestroyed;               // for bookkeeping (track cleans lists, etc.)

    public List<int> sharedTargetSteps = new List<int>();
    // --- Movement (grid-aware, dust-adjacent) ---
    [Header("Movement")]
    [SerializeField] private float minSpeed = 0.8f;     // world units/sec for longest notes
    [SerializeField] private float maxSpeed = 3.5f;     // world units/sec for shortest notes
    [SerializeField] private float arriveRadius = 0.05f;
    [SerializeField] private float chooseNextEvery = 0.15f; // seconds between neighbor picks while en route
    [SerializeField] private float lingerLongNote = 0.25f;  // extra pause for long notes at each node
    [SerializeField] private float lingerShortNote = 0.02f; // minimal pause for short notes
    [SerializeField] private float dustAdjacencyProbe = 0.42f; // radius used to detect nearby dust
    [SerializeField] private int   neighborRadiusCells = 1;    // how far from current cell to consider neighbors (4-neighbors)

    private Rigidbody2D _rb;
    private float _speed;
    private System.Random _rng;       // deterministic per-track/per-note

    static bool IsCellOccupiedByOther(Vector2Int cell, Collectable self)
    {
        if (_occupantByCell.TryGetValue(cell, out var occ) && occ != null && occ != self) return true;
        return false;
    }
    public void SetCollector(Transform collector)
    {
        _collector = collector;
    }
  private void TryBindLoopBoundary()
{
    if (!useLoopBoundaryIdea) return;
    if (assignedInstrumentTrack == null) return;

    var dt = assignedInstrumentTrack.drumTrack;
    if (dt == null) return;

    if (_boundDrumTrack == dt) return;

    // Rebind
    if (_boundDrumTrack != null)
        _boundDrumTrack.OnLoopBoundary -= HandleLoopBoundaryIdea;

    _boundDrumTrack = dt;
    _boundDrumTrack.OnLoopBoundary += HandleLoopBoundaryIdea;
}

private void UnbindLoopBoundary()
{
    if (_boundDrumTrack != null)
        _boundDrumTrack.OnLoopBoundary -= HandleLoopBoundaryIdea;

    _boundDrumTrack = null;
}

private void HandleLoopBoundaryIdea()
{
    if (!useLoopBoundaryIdea) return;
    if (_inCarry) return;

    var gfm = GameFlowManager.Instance;
    var dustGen = (gfm != null) ? gfm.dustGenerator : null;
    var dt = (assignedInstrumentTrack != null) ? assignedInstrumentTrack.drumTrack : null;
    if (dustGen == null || dt == null) return;

    Vector2 cur = (_rb != null) ? _rb.position : (Vector2)transform.position;

    _ideaDir = ChooseIdeaDirection(cur, dt, dustGen, Mathf.Max(1, ideaLookaheadCells));
    if (_ideaDir.sqrMagnitude < 0.0001f)
        _ideaDir = UnityEngine.Random.insideUnitCircle.normalized;
}

private static readonly Vector2Int[] kDirs8 = new Vector2Int[]
{
    new Vector2Int( 1, 0),
    new Vector2Int(-1, 0),
    new Vector2Int( 0, 1),
    new Vector2Int( 0,-1),
    new Vector2Int( 1, 1),
    new Vector2Int( 1,-1),
    new Vector2Int(-1, 1),
    new Vector2Int(-1,-1),
};

private static double NextDepositDspForBaseStep(
    double loopStartDsp,
    double loopLen,
    int baseSteps,
    int targetBaseStep,
    double dspNow,
    double eps = 0.010)
{
    baseSteps = Mathf.Max(1, baseSteps);
    targetBaseStep = ((targetBaseStep % baseSteps) + baseSteps) % baseSteps;

    // Which loop are we currently in since loopStart?
    double loopsSince = (dspNow - loopStartDsp) / loopLen;
    long curLoopIndex = (long)Math.Floor(loopsSince);
    if (curLoopIndex < 0) curLoopIndex = 0;

    // Position inside current loop [0..loopLen)
    double tPos = (dspNow - loopStartDsp) - (curLoopIndex * loopLen);
    tPos %= loopLen;
    if (tPos < 0) tPos += loopLen;

    double stepDur = loopLen / baseSteps;

    // Current step index (floor)
    int curStep = (int)Math.Floor(tPos / stepDur);
    curStep = Math.Clamp(curStep, 0, baseSteps - 1);

    // If target step is still ahead in this loop, use this loop; otherwise next loop.
    long depositLoopIndex = curLoopIndex + ((targetBaseStep <= curStep) ? 1 : 0);

    double deposit = loopStartDsp + depositLoopIndex * loopLen + targetBaseStep * stepDur;

    // Ensure it's safely in the future
    while (deposit <= dspNow + eps)
        deposit += loopLen;

    return deposit;
}
private Vector2 ChooseIdeaDirection(Vector2 worldPos, DrumTrack dt, CosmicDustGenerator dustGen, int lookaheadCells)
{
    int w = dt.GetSpawnGridWidth();
    int h = dt.GetSpawnGridHeight();
    if (w <= 0 || h <= 0) return Vector2.zero;

    Vector2Int c = dt.WorldToGridPosition(worldPos);
    c.x = Mathf.Clamp(c.x, 0, w - 1);
    c.y = Mathf.Clamp(c.y, 0, h - 1);

    float bestScore = float.NegativeInfinity;
    Vector2Int best = Vector2Int.zero;

    for (int d = 0; d < kDirs8.Length; d++)
    {
        var dir = kDirs8[d];
        float score = 0f;

        for (int i = 1; i <= lookaheadCells; i++)
        {
            var gp = c + dir * i;
            if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h)
                break;

            bool hasDust = dustGen.HasDustAt(gp);
            // Reward open space; penalize dust.
            score += hasDust ? -2.0f : +1.0f;

            // Immediate wall is especially bad (jail ring effect).
            if (i == 1 && hasDust)
                score -= 2.0f;
        }

        // Tiny randomness breaks ties and makes behavior feel "alive".
        score += UnityEngine.Random.Range(-0.15f, +0.15f);

        if (score > bestScore)
        {
            bestScore = score;
            best = dir;
        }
    }

    Vector2 wdir = new Vector2(best.x, best.y);
    if (wdir.sqrMagnitude > 0f) wdir.Normalize();
    return wdir;
}


    private void StartDustPocket()
    {
        if (!keepDustPocketOpen) return;
        if (isTrappedInDust) return; // Jail model.
        if (_dustPocketRoutine != null) return;
        _dustPocketRoutine = StartCoroutine(DustPocketRoutine());
    }

    private void StopDustPocket()
    {
        if (_dustPocketRoutine != null)
        {
            StopCoroutine(_dustPocketRoutine);
            _dustPocketRoutine = null;
        }
    }
    public void SetDesiredGridTarget(Vector2Int gridPos) {
        _desiredGridTarget = gridPos; 
        _hasDesiredGridTarget = true;
    }
    private bool IsInsideDustStable(Vector2 worldPos, DrumTrack dt, CosmicDustGenerator dustGen)
    {
        if (dt == null || dustGen == null) return IsPositionInsideDust(worldPos); // fallback if you must
        Vector2Int gp = dt.WorldToGridPosition(worldPos);
        return dustGen.HasDustAt(gp);
    }


    private void StartDustPocketRoutineIfNeeded()
    {
        if (!keepDustPocketOpen) return;
        if (isTrappedInDust) return;
        if (_dustPocketRoutine != null) return;
        _dustPocketRoutine = StartCoroutine(DustPocketRoutine());
    }

    private void StopDustPocketRoutineIfRunning()
    {
        if (_dustPocketRoutine == null) return;
        StopCoroutine(_dustPocketRoutine);
        _dustPocketRoutine = null;
    }

    private IEnumerator DustPocketRoutine()
    {
        // Uses DrumTrack as level authority.
        while (true)
        {
            yield return new WaitForSeconds(Mathf.Max(0.02f, dustPocketTickSeconds));

            if (!keepDustPocketOpen) continue;
            if (isTrappedInDust) continue;                 // jail model
            if (assignedInstrumentTrack == null) continue;

            var dt = assignedInstrumentTrack.drumTrack;
            if (dt == null) continue;

            var gfm = GameFlowManager.Instance;
            var dustGen = (gfm != null) ? gfm.dustGenerator : null;
            if (dustGen == null) continue;

            Vector2 cur = (_rb != null) ? _rb.position : (Vector2)transform.position;

            // We intentionally do NOT gate on "still inside dust".
            // Carving makes it "not dust" — but we still need to keep the pocket held open.
            float hold = Mathf.Max(dustPocketTickSeconds * 2f, dustPocketHoldSeconds);

            MazeArchetype phaseNow = (gfm != null && gfm.phaseTransitionManager != null)
                ? gfm.phaseTransitionManager.currentPhase
                : MazeArchetype.Establish;
            dustGen.ClaimTemporaryDiskForCollectable(
                cur,
                dustPocketRadiusWorld,
                phaseNow,
                hold,
                _dustClaimOwnerId,
                priority: 50);
        }
    }
    private void ReleaseDustPocket()
    {
        var gfm = GameFlowManager.Instance;
        var dustGen = gfm != null ? gfm.dustGenerator : null;
        var drumTrack = assignedInstrumentTrack != null ? assignedInstrumentTrack.drumTrack : null;
        if (dustGen == null || drumTrack == null) return;

        var phaseNow = (gfm != null && gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : drumTrack.GetCurrentPhaseSafe();

        float cellWorld = Mathf.Max(0.001f, drumTrack.GetCellWorldSize());
        float radiusWorld = cellWorld * 0.10f;

        // You implement this on the dust generator:
       // dustGen.ReleaseTemporaryDiskHold(transform.position, radiusWorld, phaseNow);
    }
    static bool IsCellReservedByOther(Vector2Int cell, Collectable self)
    {
        if (_reservedByCell.TryGetValue(cell, out var res) && res != null && res != self) return true;
        return false;
    }

    public static bool IsCellFreeStatic(Vector2Int cell)
    {
        lock (_lock)
        {
            if (_occupantByCell.TryGetValue(cell, out var occ) && occ != null) return false;
            if (_reservedByCell.TryGetValue(cell, out var res) && res != null) return false;
            return true;
        }
    }

    void RegisterOccupant(Vector2Int cell)
    {
        lock (_lock)
        {
            _occupantByCell[cell] = this;
        }
        _currentCell = cell;
    }

    void UnregisterOccupant()
    {
        lock (_lock)
        {
            if (_occupantByCell.TryGetValue(_currentCell, out var c) && c == this)
                _occupantByCell.Remove(_currentCell);
        }
    }

    void ClearReservation()
    {
        if (!_hasReservation) return;

        lock (_lock)
        {
            if (_reservedByCell.TryGetValue(_reservedCell, out var c) && c == this)
                _reservedByCell.Remove(_reservedCell);
        }

        _hasReservation = false;
    }

    bool TryReserveCell(Vector2Int cell)
    {
        lock (_lock)
        {
            if (IsCellOccupiedByOther(cell, this)) return false;
            if (IsCellReservedByOther(cell, this)) return false;

            _reservedByCell[cell] = this;
        }

        _reservedCell = cell;
        _hasReservation = true;
        return true;
    }

    void CommitArrival(Vector2Int arrivedCell)
    {
        // Move occupancy
        UnregisterOccupant();
        RegisterOccupant(arrivedCell);

        // If we reserved this cell, release that reservation
        if (_hasReservation && _reservedCell == arrivedCell)
            ClearReservation();
    }
    // Small helper: normalize duration ticks (large => slow)
    private float Duration01()
    {
        // Heuristic: clamp to [1..16] 16ths; you can wire drumTrack.totalSteps if you prefer.
        float t = Mathf.Clamp(noteDurationTicks, 1, 16);
        // map: 1  -> 0 (short/fast)   16 -> 1 (long/slow)
        return (t - 1f) / 15f;
    }
    
    public static bool IsCellFree(Vector2Int cell)
    {
        lock (_lock)
        {
            if (_occupantByCell.TryGetValue(cell, out var occ) && occ != null) return false;
            if (_reservedByCell.TryGetValue(cell, out var res) && res != null) return false;
            return true;
        }
    }


    private float ComputeMoveSpeed()
    {
        // Invert so long notes are slow: v = lerp(maxSpeed -> minSpeed, duration01)
        float d = Duration01();
        return Mathf.Lerp(maxSpeed, minSpeed, d);
    }

    private float ComputeLingerSeconds()
    {
        // More linger for long notes
        float d = Duration01();
        return Mathf.Lerp(lingerShortNote, lingerLongNote, d);
    }

    // stable seed so different InstrumentTracks exhibit slightly different “turn biases”
    private int StableSeed()
    {
        unchecked
        {
            int a = assignedInstrumentTrack ? assignedInstrumentTrack.GetHashCode() : 17;
            int b = assignedNote;
            int c = noteDurationTicks;
            return (a * 486187739) ^ (b * 1009) ^ (c * 9176);
        }
    }

    private bool IsPositionInsideDust(Vector2 worldPos) {
                // Robust: if ClosestPoint == query point, we are inside that collider.
        var hits = Physics2D.OverlapCircleAll(worldPos, dustAdjacencyProbe); 
        for (int i = 0; i < hits.Length; i++) {
            var dust = hits[i] ? hits[i].GetComponent<CosmicDust>() : null; 
            if (!dust) continue; 
            Vector2 cp = hits[i].ClosestPoint(worldPos); 
            if ((cp - worldPos).sqrMagnitude < 1e-6f) return true;
        } 
        return false;
    }

    private bool IsAdjacentToDust(Vector2 worldPos) { 
        // Near an edge but not inside: distance-to-edge within ring.
        var hits = Physics2D.OverlapCircleAll(worldPos, dustAdjacencyProbe * 1.35f); 
        for (int i = 0; i < hits.Length; i++) { 
            var dust = hits[i] ? hits[i].GetComponent<CosmicDust>() : null; 
            if (!dust) continue; 
            Vector2 cp = hits[i].ClosestPoint(worldPos); 
            float d = (cp - worldPos).magnitude; 
            if (d > 0.02f && d <= dustAdjacencyProbe) return true; // edge ring
        }
        return false;
    }

    private IEnumerable<Vector2Int> FourNeighbors(Vector2Int g) {
        yield return new Vector2Int(g.x + 1, g.y);
        yield return new Vector2Int(g.x - 1, g.y);
        yield return new Vector2Int(g.x, g.y + 1);
        yield return new Vector2Int(g.x, g.y - 1);
    }
    
    private IEnumerator MovementRoutine()
{
    if (!_rb && !TryGetComponent(out _rb)) yield break;

    _speed = ComputeMoveSpeed();
    const float TURB_STEP_SCALE = 0.35f;  // tune
    // Optional: trapped drift damping so notes don't "skate through dust"
    const float TRAPPED_DRIFT_MUL = 0.35f; // could be a serialized field if desired

    _rng ??= new System.Random(StableSeed());
    float seedA = (float)_rng.NextDouble() * 1000f;
    float seedB = (float)_rng.NextDouble() * 1000f;

    while (true)
    {
        yield return new WaitForFixedUpdate();
        if (_rb == null) continue;
        if (_inCarry) continue;
        var gfm = GameFlowManager.Instance;
        var dustGen = (gfm != null) ? gfm.dustGenerator : null;
        var dt = (assignedInstrumentTrack != null) ? assignedInstrumentTrack.drumTrack : null;

        Vector2 cur = _rb.position;

        bool insideDust = false;
        if (dustGen != null && dt != null)
            insideDust = IsInsideDustStable(cur, dt, dustGen);
        else
            insideDust = IsPositionInsideDust(cur); // fallback
        if (isTrappedInDust && insideDust)
        {
            // If trapped, do not drift; wait until the player/minenode carves a path.
            continue;
        }
        
        Vector2 step = Vector2.zero; 
// ------------------------------------------------------------
// LOOP-BOUNDARY IDEA BIAS
// - On each leader loop boundary, we choose an "idea direction".
// - Each FixedUpdate, we gently bias motion toward that direction.
// ------------------------------------------------------------
        _ideaDirSmoothed = Vector2.Lerp(_ideaDirSmoothed, _ideaDir, Mathf.Clamp01(Time.fixedDeltaTime * ideaTurnLerp));
        if (_ideaDirSmoothed.sqrMagnitude > 0.0001f)
        {
            Vector2 ideaStep = _ideaDirSmoothed.normalized * (_speed * ideaBiasStrength * Time.fixedDeltaTime);
            step += ideaStep;
        }

        float t = Time.time;
        float nx = Mathf.PerlinNoise(seedA, t * 0.35f) * 2f - 1f;
        float ny = Mathf.PerlinNoise(seedB, t * 0.35f) * 2f - 1f;
        Vector2 turb = new Vector2(nx, ny);
        if (turb.sqrMagnitude > 0.0001f)
            step += turb.normalized * (microTurbulenceStrength * _speed * Time.fixedDeltaTime);


        if (insideDust)
            step *= TRAPPED_DRIFT_MUL;

        float maxStep = _speed * Time.fixedDeltaTime;
        if (step.sqrMagnitude > maxStep * maxStep)
            step = step.normalized * maxStep;

        _rb.MovePosition(cur + step);
    }
}

    public int GetNote() => assignedNote;
    public bool IsDark { get; private set; } = false;
    
    public void Initialize(int note, int duration, InstrumentTrack track, NoteSet noteSet, List<int> steps)
    {
        assignedNote              = note;
        noteDurationTicks         = duration;
        assignedInstrumentTrack   = track;

        // 🔒 defensive copy — do NOT keep caller's mutable list
        sharedTargetSteps = (steps != null && steps.Count > 0)
            ? new List<int>(steps)
            : new List<int>();

        if (sharedTargetSteps.Count == 0)
            Debug.LogWarning($"{gameObject.name} - No target steps provided.");

        if (TryGetComponent(out CollectableParticles particleScript) && noteSet != null)
            particleScript.ConfigureByDuration(noteSet, duration, track);

        if (track != null)
            energySprite.color = track.trackColor;

        var explode = GetComponent<Explode>();
        if (explode != null && track != null)
        {
            // Multiply tends to look best if the prefab already has “hot” highlights.
            explode.SetTint(track.trackColor, multiply: true);
        }

        if (energySprite != null && track != null)
        {
            var c = track.trackColor;
            c.a = Mathf.Clamp01(maxAlpha);
            energySprite.color = c;

            if (_pulseRoutine != null) StopCoroutine(_pulseRoutine);
            _pulseRoutine = StartCoroutine(PulseEnergySprite());
        }

        if (assignedInstrumentTrack == null)
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");

        StartCoroutine(DarkTimeoutRoutine(track));
        if (_rb == null) TryGetComponent(out _rb);
        _dustClaimOwnerId = GetInstanceID();
        _rng ??= new System.Random(StableSeed());
        StartCoroutine(MovementRoutine());
        TryBindLoopBoundary();
        HandleLoopBoundaryIdea(); // seed an initial idea immediately

        if (!isTrappedInDust)
            StartDustPocket(); 

        // --- Occupancy initialization (Mode A: never overlap another collectable) ---
        ClearReservation();

        var dt = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null;
        if (dt != null)
        {
            // Prefer CellOf if present; otherwise your existing WorldToGridPosition is fine.
            _currentCell = dt.CellOf(transform.position);
            RegisterOccupant(_currentCell);
            var dustClaims = FindObjectOfType<DustClaimManager>();
            if (dustClaims != null)
                dustClaims.ClaimCell($"Collectable#{GetInstanceID()}", _currentCell, DustClaimType.Occupancy, seconds: -1f);
        }
        Debug.Log($"[DBG] Collectable BurstID {burstId} Track: {assignedInstrumentTrack.name} Parent: {transform.parent?.name}");
    }
    public void AttachTetherAtSpawn(Transform marker, GameObject tetherPrefabGO, Color trackColor, int durationTicksOrSteps, int anchorStep) {
        var viz = GameFlowManager.Instance ? GameFlowManager.Instance.noteViz : null;
        if (!assignedInstrumentTrack) {
            Debug.LogWarning("[Collectable] AttachTetherAtSpawn aborted: assignedInstrumentTrack is null.");
            return;
        }
        if (!viz) {
            Debug.LogWarning("[Collectable] AttachTetherAtSpawn aborted: NoteVisualizer is null.");
            return;
        }
        if (!tetherPrefabGO) {
            Debug.LogWarning("[Collectable] AttachTetherAtSpawn aborted: tether prefab is null.");
            return;
        }
        ribbonMarker = marker;
        Debug.Log($"[Collectable] AttachTetherAtSpawn track={assignedInstrumentTrack.name} " +
              $"baseStep={intendedStep} anchorStep={anchorStep} " +
              $"marker={(marker ? marker.name : "(null)")}");
    // --- Resolve anchorStep to an ABSOLUTE track step for expanded tracks ---
    int resolvedStep = anchorStep;
    try
    {
        int binSize  = Mathf.Max(1, assignedInstrumentTrack.BinSize());
        int total    = Mathf.Max(binSize, assignedInstrumentTrack.GetTotalSteps());
        int mult     = Mathf.Max(1, total / binSize); 
        int local    = ((anchorStep % binSize) + binSize) % binSize; // normalize 0..binSize-1
        // Prefer an intendedStep if it matches the same bin-local index.
        if (intendedStep >= 0 && (intendedStep % binSize) == local && intendedStep < total)
        {
            resolvedStep = intendedStep;
        }
        else
        {
            // Otherwise pick the first bin that actually has a marker for (track, absStep)
            bool found = false;
            for (int b = 0; b < mult; b++)
            {
                int abs = b * binSize + local;
                if (abs < 0 || abs >= total) continue;
                if (viz.noteMarkers != null &&
                    viz.noteMarkers.TryGetValue((assignedInstrumentTrack, abs), out var tr) && tr) {
                    resolvedStep = abs;
                    found = true;
                    break;
                }
            }
            if (!found) {
                // Last resort: if anchorStep already lies in absolute range, keep it; else map to bin 0
                if (anchorStep < 0 || anchorStep >= total) resolvedStep = local; // bin 0
                Debug.LogWarning($"[Collectable] Anchor rebind fallback: track={assignedInstrumentTrack.name} " +
                                 $"anchorStep={anchorStep}→{resolvedStep} (no existing marker found for expanded bins).");
            }
        } 
    } 
    catch (System.Exception ex)    { 
        Debug.LogWarning($"[Collectable] Anchor step resolution failed; using anchorStep={anchorStep}. {ex.Message}");
            resolvedStep = anchorStep;     
    } 
    if (tether != null)
    {
        // already attached; refresh binding
        tether.SetEndpoints(transform, ribbonMarker, trackColor, 1f);
        tether.BindByStep(assignedInstrumentTrack, resolvedStep, viz);
        return;
    }

    var go = Instantiate(tetherPrefabGO);
    go.name = $"Tether_{assignedInstrumentTrack.name}_s{resolvedStep}";
    tether = go.GetComponent<NoteTether>();
    if (!tether) tether = go.AddComponent<NoteTether>();

    // Set endpoints immediately (even if marker is temporarily unresolved)
    tether.SetEndpoints(transform, ribbonMarker, trackColor, 1f);

    // 🔑 Provide (track, step, viz) so the tether can re-find the marker if it’s not ready yet
    tether.BindByStep(assignedInstrumentTrack, resolvedStep, viz);

    // Optional: keep world space (don’t parent under UI/canvas)
    // go.transform.SetParent(null, worldPositionStays: true);

    // Particle drip hookup if present
    if (TryGetComponent(out CollectableParticles cp) && tether != null)
        cp.RegisterTether(tether, pull: 0.7f);
    }

    void Start()
    {
        isInitialized = true;
    }
    private IEnumerator DarkTimeoutRoutine(InstrumentTrack track)
    {
        var drums = track != null ? track.drumTrack : null;
        if (drums == null) yield break;

        // ~just over one loop; tweak to taste or make profile-driven
        float ttl = Mathf.Max(3f, drums.GetLoopLengthInSeconds() * 1.1f);
        yield return new WaitForSeconds(ttl);

        // Become "dark" (muted look), but keep collider & collection live
        IsDark = true;

        if (energySprite != null)
        {
            var c = energySprite.color;
            // desaturate & lower alpha a bit
            var grey = new Color(0.25f, 0.25f, 0.25f, Mathf.Clamp01(c.a * 0.55f));
            energySprite.color = grey;
        }

        // If we have a marker, ensure it’s greyed
        if (ribbonMarker != null)
        {
            var ml = ribbonMarker.GetComponent<MarkerLight>() ?? ribbonMarker.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(new Color(1f, 1f, 1f, 0.18f));
        }
    }

   private IEnumerator TravelRoutine(
    double depositDspTime,
    int durationTicks,
    float force,
    Action onArrived)
{
    _inCarry = true;

    // non-physical immediately
    if (_rb != null) _rb.simulated = false;
    if (TryGetComponent(out Collider2D col)) col.enabled = false;

    var ps = GetComponentInChildren<ParticleSystem>();
    if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

    // ----- Parent to vehicle ("children in back seat") -----
    if (_collector != null)
    {
        _carryParent = _collector;

        transform.SetParent(_carryParent, worldPositionStays: true);

        Vector3 jitter = (carryLocalOffsetJitter > 0f)
            ? (Vector3)(UnityEngine.Random.insideUnitCircle * carryLocalOffsetJitter)
            : Vector3.zero;

        transform.localPosition = carryLocalOffset + jitter;
        transform.localRotation = Quaternion.identity;
    }

    // ----- DSP timing -----
    double dspNow = AudioSettings.dspTime;

    // If deposit time is already passed (or basically now), snap immediately.
    if (depositDspTime <= dspNow + 0.00075)
    {
        transform.SetParent(null, worldPositionStays: true);
        _carryParent = null;

        if (ribbonMarker) transform.position = ribbonMarker.position;
        onArrived?.Invoke();

        _inCarry = false;
        yield break;
    }

    // Decide travel duration purely as a function of remaining DSP time.
    // (No step math, no deltaTime.)
    double timeUntilDeposit = depositDspTime - dspNow;

    float desiredTravel = Mathf.Clamp(depositTravelSeconds, minDepositTravelSeconds, maxDepositTravelSeconds);

    // Travel cannot exceed available time.
    float travelSeconds = Mathf.Clamp(desiredTravel, 0.02f, (float)timeUntilDeposit);

    // Compute launch time. If too close, push launch forward (shrinks carry) but STILL land on time.
    double travelStartDsp = depositDspTime - travelSeconds;

    // Ensure we don't start "late" due to frame timing.
    double minLaunch = dspNow + carryMinLeadSeconds;  // you already have this knob
    if (travelStartDsp < minLaunch)
        travelStartDsp = minLaunch;

    // Recompute travelSeconds from the clamped launch so u maps correctly.
    travelSeconds = (float)(depositDspTime - travelStartDsp);
    if (travelSeconds < 0.02f) travelSeconds = 0.02f; // guard; snap will still enforce landing

    // ----- Hold as child until launch moment (DSP-driven) -----
    while (_carryParent != null && AudioSettings.dspTime < travelStartDsp)
        yield return null;

    // ----- Detach and travel -----
    transform.SetParent(null, worldPositionStays: true);
    _carryParent = null;

    // If tether vanished, just wait and snap at DSP time.
    if (tether == null)
    {
        while (AudioSettings.dspTime < depositDspTime)
            yield return null;

        if (ribbonMarker) transform.position = ribbonMarker.position;
        onArrived?.Invoke();

        _inCarry = false;
        yield break;
    }

    // DSP-driven interpolation from travelStartDsp -> depositDspTime.
    // SmoothStep gives clean motion with less end “snap velocity” than cubic-in.
    double dur = System.Math.Max(0.0005, depositDspTime - travelStartDsp);

    while (tether != null)
    {
        double now = AudioSettings.dspTime;
        if (now >= depositDspTime)
            break;

        float lin = (float)((now - travelStartDsp) / dur);
        lin = Mathf.Clamp01(lin);

        // SmoothStep: smooth accel/decel, avoids “bouncy fast end” that can skip frames.
        float u = lin * lin * (3f - 2f * lin);

        transform.position = tether.EvaluatePosition01(u);
        yield return null;
    }

    // Authoritative landing moment.
    if (ribbonMarker) transform.position = ribbonMarker.position;

    onArrived?.Invoke();

    _inCarry = false;

    var ml = ribbonMarker ? ribbonMarker.GetComponent<MarkerLight>() : null;
    if (ml) ml.LightUp(tether != null ? tether.baseColor : Color.white);

    ReportedCollected = true;
    NotifyDestroyedOnce();

    if (tether) Destroy(tether.gameObject);
    Destroy(gameObject);
} 
public void BeginCarryThenDepositAtDsp(
    double depositDspTime,
    int durationTicks,
    float force,
    Action onArrived)
{
    if (_carryRoutine != null) StopCoroutine(_carryRoutine);
    _carryRoutine = StartCoroutine(CarryAndDepositRoutine(depositDspTime, durationTicks, force, onArrived));
}

    private IEnumerator PulseEnergySprite()
    {
        Vector3 startScale = transform.localScale;
        while (true)
        {
            float t = (Mathf.Sin(Time.time * pulseSpeed) * 0.5f) + 0.5f; // 0..1
            float a = Mathf.Lerp(minAlpha, maxAlpha, t);
            if (energySprite != null)
            {
                var col = energySprite.color; col.a = a; energySprite.color = col;
            }
            transform.localScale = startScale * Mathf.Lerp(1f, pulseScale, t);
            yield return null;
        }
    }
    private void NotifyDestroyedOnce()
    {
        if (_destroyNotified) return;
        _destroyNotified = true;
        OnDestroyed?.Invoke();
    }
    private void OnEnable()
    {
        _handled = false;
        _destroyNotified = false;
        if (isInitialized) StartDustPocketRoutineIfNeeded();
    }
    private void StopDustPocketAndReleaseClaims()
    {
        if (_dustPocketRoutine != null)
        {
            StopCoroutine(_dustPocketRoutine);
            _dustPocketRoutine = null;
        }

        // If you have an owner id / claim owner string, release it here.
        // Example patterns you likely already have:
        // dustGen?.ReleaseClaimsForOwner(_dustClaimOwnerId);
        // or dustClaims?.ReleaseOwner(ownerString);

        _dustClaimOwnerId = 0;
    }

    private void OnDestroy()
    {
        ClearReservation();
        UnbindLoopBoundary();
        UnregisterOccupant();
        UnregisterCarryOrbit();
        StopDustPocket();
        var dustClaims = FindObjectOfType<DustClaimManager>();
        if (dustClaims != null)
            dustClaims.ReleaseOwner($"Collectable#{GetInstanceID()}");
        NotifyDestroyedOnce();
    }

    private void OnDisable()
    {
        ClearReservation();
        UnbindLoopBoundary();
        StopDustPocketRoutineIfRunning();
        UnregisterOccupant();
        StopDustPocket();
        ReleaseDustPocket();
        var dustClaims = FindObjectOfType<DustClaimManager>();
        if (dustClaims != null)
            dustClaims.ReleaseOwner($"Collectable#{GetInstanceID()}");

        NotifyDestroyedOnce();
    } // pooling-safe
    
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (_rb == null && !TryGetComponent(out _rb)) return;

        // Dust should be a physical obstacle for collectables.
        // We apply a small impulse away from the dust on entry to avoid deep contact jitter.
        if (coll.collider != null && coll.collider.GetComponent<CosmicDust>() != null)
        {
            Vector2 away = Vector2.zero;

            // Prefer contact normal if available.
            if (coll.contactCount > 0)
            {
                // Contact normal points from other collider to this collider in 2D collisions.
                away = coll.GetContact(0).normal;
            }
            if (away.sqrMagnitude < 0.0001f)
            {
                away = (_rb.position - (Vector2)coll.collider.bounds.center);
            }
            if (away.sqrMagnitude < 0.0001f)
            {
                away = UnityEngine.Random.insideUnitCircle;
            }

            away.Normalize();
            _rb.AddForce(away * dustCollisionEnterImpulse, ForceMode2D.Impulse);

            // Prevent rapid-fire escape impulses in the movement loop.
            _dustEscapeCooldown = dustEscapeCooldownSeconds;
        }
    }

    private void OnCollisionStay2D(Collision2D coll)
    {
        if (_rb == null) return;
        if (coll.collider == null) return;

        if (coll.collider.GetComponent<CosmicDust>() == null) return;

        // Continuous repulsion while in contact reduces frictiony "sticking" even with low-friction materials.
        Vector2 n = Vector2.zero;
        if (coll.contactCount > 0) n = coll.GetContact(0).normal;
        if (n.sqrMagnitude < 0.0001f) n = (_rb.position - (Vector2)coll.collider.bounds.center);

        if (n.sqrMagnitude > 0.0001f)
        {
            n.Normalize();

            // Remove velocity component pushing into dust.
            float into = Vector2.Dot(_rb.linearVelocity, -n);
            if (into > 0f) _rb.linearVelocity += n * into;

            _rb.AddForce(n * dustCollisionStayForce, ForceMode2D.Force);
        }
    }

private void RegisterCarryOrbit()
{
    if (_collector == null || _registeredInCarryOrbit) return;

    if (!_carryOrbitByCollector.TryGetValue(_collector, out var list) || list == null)
    {
        list = new List<Collectable>();
        _carryOrbitByCollector[_collector] = list;
    }

    if (!list.Contains(this))
        list.Add(this);

    _registeredInCarryOrbit = true;
    _carryOrbitPhaseOffset = UnityEngine.Random.Range(0f, Mathf.PI * 2f);

    RefreshCarryOrbitIndices(_collector);
}

private void UnregisterCarryOrbit()
{
    if (_collector == null || !_registeredInCarryOrbit) return;

    if (_carryOrbitByCollector.TryGetValue(_collector, out var list) && list != null)
    {
        list.Remove(this);

        if (list.Count == 0)
            _carryOrbitByCollector.Remove(_collector);
        else
            RefreshCarryOrbitIndices(_collector);
    }

    _registeredInCarryOrbit = false;
    _carryOrbitIndex = -1;
}

private static void RefreshCarryOrbitIndices(Transform collector)
{
    if (collector == null) return;
    if (!_carryOrbitByCollector.TryGetValue(collector, out var list) || list == null) return;

    // Compact and re-index so orbit slots are stable and evenly spaced
    for (int i = list.Count - 1; i >= 0; i--)
        if (list[i] == null) list.RemoveAt(i);

    for (int i = 0; i < list.Count; i++)
        if (list[i] != null) list[i]._carryOrbitIndex = i;
}

private Vector3 ComputeCarryOrbitTargetWorld()
{
    if (_collector == null) return transform.position;

    int count = 1;
    if (_carryOrbitByCollector.TryGetValue(_collector, out var list) && list != null)
        count = Mathf.Max(1, list.Count);

    // Even angular distribution around the vehicle
    float slotAngle = (Mathf.PI * 2f) * (_carryOrbitIndex / (float)count);

    // Rotate over time for “orbit” feel
    float t = Time.time;
    float angle = _carryOrbitPhaseOffset + slotAngle + t * carryOrbitAngularSpeed;

    Vector3 center = _collector.position;
    Vector3 offset = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * carryOrbitRadius;

    // Optional readability bias (slightly above the vehicle)
    offset.y += carryOrbitVerticalBias;

    return center + offset;
}


/// <summary>
/// Compute normalized ribbon X (0..1) for the ribbonMarker against the ribbon UI width.
/// This is the “visual truth” that the playhead line is moving across.
/// </summary>
private bool TryGetRibbonU01(out double u01)
{
    u01 = 0.0;

    if (ribbonMarker == null)
        return false;

    // You need a RectTransform that defines the ribbon width in world/canvas space.
    // Commonly this is the parent of your marker(s) or the same root as playheadLine.
    // Try: marker’s parent rect.
    var parentRect = ribbonMarker.parent as RectTransform;
    if (parentRect == null)
        return false;

    // Convert marker world position into parent local space, then normalize by width.
    Vector2 local;
    RectTransformUtility.ScreenPointToLocalPointInRectangle(
        parentRect,
        RectTransformUtility.WorldToScreenPoint(null, ribbonMarker.position),
        null,
        out local
    );

    float w = parentRect.rect.width;
    if (w <= 0.0001f)
        return false;

    // parentRect local X typically ranges [-w/2, +w/2]
    double x01 = (local.x / w) + 0.5;
    // clamp to [0,1)
    x01 = Math.Max(0.0, Math.Min(0.999999, x01));

    u01 = x01;
    return true;
}
public void BeginCarryAndDepositAtDsp(
    double depositDspTime,
    int durationTicks,
    float force,
    Action onArrived)
{
    if (_carryRoutine != null) StopCoroutine(_carryRoutine);
    _carryRoutine = StartCoroutine(CarryAndDepositRoutine(depositDspTime, durationTicks, force, onArrived));
}

private IEnumerator CarryAndDepositRoutine(
    double depositDspTime,
    int durationTicks,
    float force,
    Action onArrived)
{
    _inCarry = true;

    // non-physical immediately
    if (_rb != null) _rb.simulated = false;
    if (TryGetComponent(out Collider2D col)) col.enabled = false;

    var ps = GetComponentInChildren<ParticleSystem>();
    if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

    // ----- Parent to vehicle ("children in back seat") -----
    if (_collector != null)
    {
        _carryParent = _collector;

        transform.SetParent(_carryParent, worldPositionStays: true);

        Vector3 jitter = (carryLocalOffsetJitter > 0f)
            ? (Vector3)(UnityEngine.Random.insideUnitCircle * carryLocalOffsetJitter)
            : Vector3.zero;

        transform.localPosition = carryLocalOffset + jitter;
        transform.localRotation = Quaternion.identity;
    }

    // ----- DSP timing -----
    double dspNow = AudioSettings.dspTime;

    // If deposit time is already passed (rare), snap immediately.
    if (depositDspTime <= dspNow + 0.0005)
    {
        transform.SetParent(null, worldPositionStays: true);
        _carryParent = null;

        if (ribbonMarker) transform.position = ribbonMarker.position;
        onArrived?.Invoke();
        _inCarry = false;
        yield break;
    }

    float desiredTravel = Mathf.Clamp(depositTravelSeconds, minDepositTravelSeconds, maxDepositTravelSeconds);

    // available time until deposit
    double timeUntilDeposit = depositDspTime - dspNow;

    // travel cannot exceed available time
    float travelSeconds = Mathf.Clamp(desiredTravel, 0.02f, (float)timeUntilDeposit);

    // launch moment: deposit minus travel duration
    double travelStartDsp = depositDspTime - travelSeconds;

    // Ensure we don't start "late" due to frame timing.
    // If we're already too close, shorten travel by pushing start forward (still lands exactly).
    double minLaunch = dspNow + carryMinLeadSeconds;
    if (travelStartDsp < minLaunch)
        travelStartDsp = minLaunch;

    // ----- Hold as child until launch moment (DSP-driven) -----
    while (_carryParent != null && AudioSettings.dspTime < travelStartDsp)
        yield return null;

    // ----- Detach and travel -----
    transform.SetParent(null, worldPositionStays: true);
    _carryParent = null;

    // DSP-authoritative travel. Lands EXACTLY at depositDspTime.
    yield return StartCoroutine(TravelRoutine(depositDspTime, durationTicks, force, onArrived));
    _inCarry = false;
}
private static double EffectiveLoopStart(double transportStartDsp, double loopLen, double dspNow)
{
    if (transportStartDsp <= 0.0 || loopLen <= 0.0) return 0.0;

    // Number of whole loops elapsed since transportStartDsp (floor handles partial)
    double loopsElapsed = Math.Floor((dspNow - transportStartDsp) / loopLen);

    // Most recent loop boundary (<= dspNow)
    double effective = transportStartDsp + loopsElapsed * loopLen;

    // Guard: due to floating error, don't let it drift ahead of dspNow
    if (effective > dspNow) effective -= loopLen;

    return effective;
}
   private void OnTriggerEnter2D(Collider2D coll)
{
    var vehicle = coll.GetComponent<Vehicle>();
    if (vehicle == null || _handled) return;

    _collector = vehicle.transform;

    // ---- Validate we can actually process a collection BEFORE latching _handled ----
    if (assignedInstrumentTrack == null)
    {
        Debug.LogWarning($"[COLLECT] Ignored: assignedInstrumentTrack is null (name={name}).");
        return;
    }

    var drumTrack = assignedInstrumentTrack.drumTrack;
    if (drumTrack == null)
    {
        Debug.LogWarning($"[COLLECT] Ignored: track '{assignedInstrumentTrack.name}' has no DrumTrack yet (name={name}).");
        return;
    }

    // Transport + loop sanity
    double dspNow  = AudioSettings.dspTime;

    double loopLen = drumTrack.GetLoopLengthInSeconds();
    if (loopLen <= 0.0)
    {
        Debug.LogWarning($"[COLLECT] Deferred: loopLen <= 0 (loopLen={loopLen:F6}) name={name}");
        return; // do NOT latch _handled
    }

    // IMPORTANT: leaderStart/start are "transport origins". Convert to the most recent loop boundary.
    double transportStart = drumTrack.leaderStartDspTime;
    if (transportStart <= 0.0) transportStart = drumTrack.startDspTime;
    if (transportStart <= 0.0)
    {
        Debug.LogWarning($"[COLLECT] Deferred: drum transport not anchored yet (leaderStart/start <= 0). name={name}");
        return; // do NOT latch _handled
    }

    // Snap to the current loop boundary so we match what the player hears.
    double loopStart = EffectiveLoopStart(transportStart, loopLen, dspNow);
    if (loopStart <= 0.0)
    {
        Debug.LogWarning($"[COLLECT] Deferred: effective loopStart invalid. name={name}");
        return; // do NOT latch _handled
    }

    // Now we can safely latch idempotency + switch to carry behavior.
    _handled = true;

    // We are now "collected": stop dust pocket & claims and enter carry mode immediately.
    StopDustPocketAndReleaseClaims();
    RegisterCarryOrbit();

    // ------------------------------------------------------------
    // STEP MATCHING (anchor-consistent)
    // ------------------------------------------------------------
    int baseSteps   = Mathf.Max(1, drumTrack.totalSteps);
    int leaderSteps = Mathf.Max(1, drumTrack.GetLeaderSteps());

    // We expect leaderSteps to be an integer multiple of baseSteps.
    int mul = Mathf.Max(1, Mathf.RoundToInt(leaderSteps / (float)baseSteps));

    // Position within current loop [0, loopLen)
    double tPos = dspNow - loopStart;
    if (tPos < 0) tPos = 0;
    if (tPos >= loopLen) tPos %= loopLen;

    // Leader step duration (seconds)
    double leaderStepDur = loopLen / leaderSteps;
    // Window is in *time*, derived from authored step window in *leader steps*.
    // Compare against STEP CENTERS (not onsets) so collisions inside a step bias toward the step
    // that is actually "playing" rather than the previous onset.
    double timingWin = leaderStepDur * (drumTrack.timingWindowSteps * mul) * 0.5;
    int matchedStep = -1; 
    double bestAbs = timingWin; 
    double bestSigned = double.NegativeInfinity; // tie-breaker
    for (int i = 0; i < sharedTargetSteps.Count; i++) { 
        int baseStep = sharedTargetSteps[i];
        // Base step projected into leader timeline.
        int leaderStep = baseStep * mul;
        // Center of step window.
        double stepCenterPos = ((leaderStep + 0.5) * leaderStepDur) % loopLen;
        
        // Signed delta wrapped to [-loopLen/2, +loopLen/2]
        double signed = stepCenterPos - tPos;
        if (signed > loopLen * 0.5) signed -= loopLen;
        else if (signed < -loopLen * 0.5) signed += loopLen; 
        double abs = System.Math.Abs(signed);
        
        // Primary: smallest abs delta within window.
        // // Tie-break: prefer FUTURE (positive signed) so we don’t go “one behind” on boundaries.
        if (abs < bestAbs || (System.Math.Abs(abs - bestAbs) < 1e-9 && signed > bestSigned)) {
            bestAbs = abs; 
            bestSigned = signed; 
            matchedStep = baseStep;
        }
    }

    // ------------------------------------------------------------
    // SUCCESS PATH: immediate confirmation + immediate loop write
    // ------------------------------------------------------------
    float force = vehicle.GetForceAsMidiVelocity();
    vehicle.CollectEnergy(amount);

    int stepToReportBase =
        (matchedStep >= 0) ? matchedStep :
        (intendedStep >= 0) ? (((intendedStep % baseSteps) + baseSteps) % baseSteps) :
        0;
// ----- TRACE: collision timing vs transport -----
    double dspNowDbg = AudioSettings.dspTime;
    double loopLenDbg = drumTrack.GetLoopLengthInSeconds();
    double transportStartDbg = drumTrack.leaderStartDspTime > 0 ? drumTrack.leaderStartDspTime : drumTrack.startDspTime;

    int baseStepsDbg = Mathf.Max(1, drumTrack.totalSteps);
    int leaderStepsDbg = Mathf.Max(1, drumTrack.GetLeaderSteps());
    int mulDbg = Mathf.Max(1, Mathf.RoundToInt(leaderStepsDbg / (float)baseStepsDbg));

    double loopStartDbg = EffectiveLoopStart(transportStartDbg, loopLenDbg, dspNowDbg);
    double tPosDbg = (dspNowDbg - loopStartDbg) % loopLenDbg; if (tPosDbg < 0) tPosDbg += loopLenDbg;

    double baseStepDurDbg = loopLenDbg / baseStepsDbg;
    double leaderStepDurDbg = loopLenDbg / leaderStepsDbg;

    int playheadBaseStepDbg = (int)Math.Floor(tPosDbg / baseStepDurDbg) % baseStepsDbg;
    int playheadLeaderStepDbg = (int)Math.Floor(tPosDbg / leaderStepDurDbg) % leaderStepsDbg;

    int reportedBase = stepToReportBase;
    int reportedLeader = reportedBase * mulDbg;

    double reportedBasePos = (reportedBase * baseStepDurDbg) % loopLenDbg;
    double reportedLeaderPos = (reportedLeader * leaderStepDurDbg) % loopLenDbg;

    Debug.Log(
        $"[COLLECT_TRACE] name={name} dspNow={dspNowDbg:F6} loopLen={loopLenDbg:F6} " +
        $"transportStart={(transportStartDbg):F6} loopStart={loopStartDbg:F6} tPos={tPosDbg:F6} " +
        $"baseSteps={baseStepsDbg} leaderSteps={leaderStepsDbg} mul={mulDbg} " +
        $"playheadBase={playheadBaseStepDbg} playheadLeader={playheadLeaderStepDbg} " +
        $"reportedBase={reportedBase} reportedLeader={reportedLeader} " +
        $"repBasePos={reportedBasePos:F6} repLeaderPos={reportedLeaderPos:F6} " +
        $"intendedStep={intendedStep} matchedStep={matchedStep}"
    );
    float velocity127 = ComputeHitVelocity127(vehicle);

    // Confirm should happen at collision point (immediate), not at deposit.
    OnCollected?.Invoke(noteDurationTicks, force);

    // Write to the track immediately (so loop state updates now).
    assignedInstrumentTrack.OnCollectableCollected(this, stepToReportBase, noteDurationTicks, velocity127);
    Debug.Log($"[WRITE_TRACE] track={name} got stepBase={stepToReportBase} totalSteps={drumTrack.totalSteps} " +
              $"leaderSteps={drumTrack.GetLeaderSteps()} dspNow={AudioSettings.dspTime:F6}");

    // Non-physical carry: disable RB sim, disable collider, stop explode permanence
    if (_rb == null) TryGetComponent(out _rb);
    if (_rb != null) _rb.simulated = false;

    if (TryGetComponent(out Collider2D col)) col.enabled = false;

    var explode = GetComponent<Explode>();
    if (explode != null) explode.Permanent(false);

    // ------------------------------------------------------------
    // VISUAL DEPOSIT: schedule at next occurrence of the chosen base step
    // ------------------------------------------------------------
    // Re-sample time in case the above took a frame; also recompute effective boundary.
    dspNow  = AudioSettings.dspTime;
    loopLen = drumTrack.GetLoopLengthInSeconds();
    if (loopLen <= 0.0)
    {
        if (_carryRoutine != null) return;
        BeginCarryAndDepositAtDsp(dspNow + minDepositTravelSeconds, noteDurationTicks, force, onArrived: null);
        Debug.LogWarning($"[COLLECT] loopLen <= 0 while scheduling deposit; fallback travel. name={name}");
        return;
    }

    transportStart = drumTrack.leaderStartDspTime;
    if (transportStart <= 0.0) transportStart = drumTrack.startDspTime;

    if (transportStart <= 0.0)
    {
        Debug.LogWarning($"[COLLECT] No valid transportStart; carrying without timed deposit. name={name}");
        BeginCarryAndDepositAtDsp(dspNow + minDepositTravelSeconds, noteDurationTicks, force, onArrived: null);
        return;
    }

    loopStart = EffectiveLoopStart(transportStart, loopLen, dspNow);

    double depositDsp = NextDepositDspForBaseStep(loopStart, loopLen, baseSteps, stepToReportBase, dspNow);

    // Ensure we have enough time for travel; if not, push to next loop.
    double minNeeded = minDepositTravelSeconds + 0.01;
    while ((depositDsp - dspNow) < minNeeded)
        depositDsp += loopLen;
    if (_carryRoutine != null) return;

    BeginCarryAndDepositAtDsp(depositDsp, noteDurationTicks, force, onArrived: null);

    // Optional debug: confirm phase alignment (can remove later)
    double baseStepDur = loopLen / baseSteps;
    int playheadBaseStep = (int)Math.Floor(((dspNow - loopStart + loopLen) % loopLen) / baseStepDur) % baseSteps;

    Debug.Log($"[COLLECT] baseStep={stepToReportBase} playheadBaseStep={playheadBaseStep}/{baseSteps} " +
              $"depositIn={(depositDsp - dspNow):F3}s loopLen={loopLen:F3}s tPos={(dspNow - loopStart):F3}s name={name}");
} 
    private float ComputeHitVelocity127(Vehicle vehicle)
    {
        if (vehicle == null) return 127f;

        // Vehicle RB must exist.
        var vRb = vehicle.rb; // use your existing Vehicle.rb reference
        if (vRb == null) return 127f;

        // Relative velocity (if collectable can move). If collectable has no RB, treat as stationary.
        Vector2 relVel = vRb.linearVelocity;
        if (_rb != null)
            relVel -= _rb.linearVelocity;

        // Direction from vehicle -> collectable.
        Vector2 to = (Vector2)transform.position - vRb.position;
        float dist = to.magnitude;
        if (dist > 0.0001f) to /= dist;
        else to = Vector2.zero;

        // Only count motion INTO the collectable.
        float approachSpeed = Mathf.Max(0f, Vector2.Dot(relVel, to));

        // Define what "max hit" means. arcadeMaxSpeed is usually too easy to hit constantly;
        // use a higher value to avoid pegging.
        float maxApproach = Mathf.Max(0.01f, vehicle.arcadeMaxSpeed * 1.5f);

        float x = Mathf.Clamp01(approachSpeed / maxApproach);

        // Optional: more resolution at low/mid hits.
        x = Mathf.Pow(x, 0.7f);

        float v127 = Mathf.Lerp(60f, 120f, x);

        Debug.Log($"[COLLECT:HIT] approach={approachSpeed:F2} maxA={maxApproach:F2} x={x:F2} v127={v127:F1} " +
                  $"vehSpeed={vRb.linearVelocity.magnitude:F2} arcadeMaxSpeed={vehicle.arcadeMaxSpeed:F2}");

        return v127;
    }

}
