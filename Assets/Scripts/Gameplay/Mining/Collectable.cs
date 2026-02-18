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

    // Desired SpawnGrid target (Option A): used by DebrisRoutine to bias motion within the SpawnGrid.
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
    private IEnumerator TravelRoutine(int durationTicks, float force, float seconds, Action onArrived)
    {
        if (TryGetComponent(out Collider2D col)) col.enabled = false;
        var ps = GetComponentInChildren<ParticleSystem>();
        if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        float t = 0f;
        while (t < seconds && tether)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / seconds);
            transform.position = tether.EvaluatePosition01(u);
            yield return null;
        }
        if (ribbonMarker) transform.position = ribbonMarker.position;
        onArrived?.Invoke();
        _inCarry = false;
        var ml = ribbonMarker ? ribbonMarker.GetComponent<MarkerLight>() : null;
        if (ml) ml.LightUp(tether != null ? tether.baseColor : Color.white);

        ReportedCollected = true; 
        OnCollected?.Invoke(durationTicks, force); // ✅ event raised inside Collectable
        NotifyDestroyedOnce();
        if (tether) Destroy(tether.gameObject);
        Destroy(gameObject);
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
    public void BeginCarryThenDeposit(double depositDspTime, float leadSeconds, int durationTicks, float force, Action onArrived)
    {
        StopDustPocketAndReleaseClaims();
        if (_carryRoutine != null) StopCoroutine(_carryRoutine);
        _carryRoutine = StartCoroutine(CarryThenDepositRoutine(depositDspTime, leadSeconds, durationTicks, force, onArrived));
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
    private IEnumerator CarryThenDepositRoutine(double depositDspTime, float leadSeconds, int durationTicks, float force, Action onArrived)
    {
        _inCarry = true;
        if (_rb != null) _rb.simulated = false;
        // Make it non-collectable immediately.
        if (TryGetComponent(out Collider2D col)) col.enabled = false;

        // Optional: stop any particle emission while carried
        var ps = GetComponentInChildren<ParticleSystem>();
        if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        // Carry until just before the deposit moment.
        double startDepositDsp = depositDspTime - Math.Max(0.0, (double)leadSeconds);

        while (_collector != null && AudioSettings.dspTime < startDepositDsp)
        {
            // Orbit around the vehicle while carried
            Vector3 target = ComputeCarryOrbitTargetWorld();
            transform.position = Vector3.Lerp(transform.position, target, carryOrbitFollowLerp * Time.deltaTime);
            yield return null;
        }
        UnregisterCarryOrbit();
        // Deposit travel should finish on-beat.
        float seconds = Mathf.Max(0.02f, leadSeconds);
        yield return StartCoroutine(TravelRoutine(durationTicks, force, seconds, onArrived));
    }
    private void OnTriggerEnter2D(Collider2D coll)
    {
        var vehicle = coll.GetComponent<Vehicle>();
        if (vehicle == null || _handled) return; 
        // ---- Validate we can actually process a collection BEFORE latching _handled ----
        if (assignedInstrumentTrack == null) { 
            Debug.LogWarning($"[COLLECT] Ignored: assignedInstrumentTrack is null (name={name})."); 
            return; }
        var drumTrack = assignedInstrumentTrack.drumTrack; 
        if (drumTrack == null) { 
            Debug.LogWarning($"[COLLECT] Ignored: track '{assignedInstrumentTrack.name}' has no DrumTrack yet (name={name})."); 
            return;
        }
        // Anchor to what the player hears. During phase/motif setup this can briefly be 0.
        double loopStart = drumTrack.leaderStartDspTime; 
        if (loopStart <= 0.0) 
            loopStart = drumTrack.startDspTime; // fallback for early phase setup
        if (loopStart <= 0.0) {
            // IMPORTANT: Do NOT latch _handled; let the player collect once timing is ready.
            Debug.LogWarning($"[COLLECT] Deferred: drum transport not anchored yet (leaderStartDspTime/startDspTime <= 0). name={name}"); 
            return;
        }
        
        // Now we can safely latch idempotency + switch to carry behavior._collector = vehicle.transform;
        _handled = true; 
        StopDustPocketAndReleaseClaims(); 
        RegisterCarryOrbit();
        
        // (Optional availability check)
        // if (_availableUntilDsp > 0 && AudioSettings.dspTime > _availableUntilDsp) { OnFailedCollect(); return; }

        double dspNow  = AudioSettings.dspTime;
        double loopLen = drumTrack.GetLoopLengthInSeconds();

        int baseSteps   = Mathf.Max(1, drumTrack.totalSteps);
        int leaderSteps = Mathf.Max(1, drumTrack.GetLeaderSteps());

// We expect leaderSteps to be an integer multiple of baseSteps.
        int mul = Mathf.Max(1, Mathf.RoundToInt(leaderSteps / (float)baseSteps));
        
// Position in the *leader* loop timeline [0, loopLen)
        double tPos = (dspNow - loopStart) % loopLen;
        if (tPos < 0) tPos += loopLen;

// Leader step duration (seconds)
        double leaderStepDur = loopLen / leaderSteps;

// Timing window expressed in leader steps, derived from your existing setting.
// If timingWindowSteps is defined in base steps, multiply it by mul; if it’s already
// leader-steps-based, leave it alone. Given your current code used base stepDur,
// it likely represents "base steps", so multiply.
        double timingWin = leaderStepDur * (drumTrack.timingWindowSteps * mul) * 0.5;

        int matchedStep = -1;
        double bestErr = timingWin;

        for (int i = 0; i < sharedTargetSteps.Count; i++)
        {
            int baseStep = sharedTargetSteps[i];

            // Base step projected into leader timeline.
            int leaderStep = baseStep * mul;

            // Convert to position in seconds within loop.
            double stepPos = (leaderStep * leaderStepDur) % loopLen;

            double delta = Math.Abs(stepPos - tPos);
            if (delta > loopLen * 0.5) delta = loopLen - delta;

            if (delta < bestErr)
            {
                bestErr = delta;
                matchedStep = baseStep; // keep returning base index
            }
        }

            // success path
            float force = vehicle.GetForceAsMidiVelocity();
            vehicle.CollectEnergy(amount);
            Debug.Log("[COLLECT] Collectable Triggered");
            int stepToReport = (intendedStep >= 0) ? intendedStep : (matchedStep  >= 0) ? matchedStep  : 0; // defensive: never send -1
            float velocity127 = ComputeHitVelocity127(vehicle);
            assignedInstrumentTrack.OnCollectableCollected(this, stepToReport, noteDurationTicks, velocity127);

            if (TryGetComponent(out Collider2D col)) col.enabled = false;
            var explode = GetComponent<Explode>();
            if(explode != null) explode.Permanent(false);

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
