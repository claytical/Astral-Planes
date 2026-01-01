using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class Collectable : MonoBehaviour
{
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public SpriteRenderer energySprite;
    public int burstId;

    // ---- Dust collision tuning ----
    public float dustCollisionEnterImpulse = 0.85f;
    public float dustCollisionStayForce = 2.25f;
    public float dustEscapeCooldownSeconds = 0.35f;
    private float _dustEscapeCooldown;

    // Desired SpawnGrid target (Option A): used by DebrisRoutine to bias motion within the SpawnGrid.
    private Vector2Int _desiredGridTarget;
    private bool _hasDesiredGridTarget;

    public void SetDesiredGridTarget(Vector2Int targetCell)
    {
        _desiredGridTarget = targetCell;
        _hasDesiredGridTarget = true;
    }
 
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

    Vector2Int _currentCell;
    Vector2Int _reservedCell;
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
    private Vector3 _targetWorld;     // current node center to walk to
    private bool _hasTarget;
    private float _speed;
    private System.Random _rng;       // deterministic per-track/per-note

    static bool IsCellOccupiedByOther(Vector2Int cell, Collectable self)
    {
        if (_occupantByCell.TryGetValue(cell, out var occ) && occ != null && occ != self) return true;
        return false;
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

 bool TryPickNextGridNode(out Vector3 worldCenter)
{
    worldCenter = transform.position;
    var dt = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null;
    if (dt == null) return false;

    // If we’re replanning, discard any previous reservation.
    ClearReservation();

    Vector2Int here = dt.WorldToGridPosition(transform.position);

    // Collect candidate CELLS (not world positions) so we can enforce occupancy.
    var candidateCells = new List<Vector2Int>(8);
    foreach (var n in FourNeighbors(here))
    {
        // Mode A: never move into an occupied/reserved cell
        if (!IsCellFree(n)) continue; // your instance helper

        Vector3 c = dt.GridToWorldPosition(n);
        if (IsPositionInsideDust(c)) continue;
        if (!IsAdjacentToDust(c)) continue;
        candidateCells.Add(n);
    }

    // Fallback: any 4-neighbor that isn’t inside dust (still respecting IsCellFree)
    if (candidateCells.Count == 0)
    {
        foreach (var n in FourNeighbors(here))
        {
            if (!IsCellFree(n)) continue;

            Vector3 c = dt.GridToWorldPosition(n);
            if (!IsPositionInsideDust(c))
                candidateCells.Add(n);
        }
    }

    if (candidateCells.Count == 0) return false;

    _rng ??= new System.Random(StableSeed());

    // Score and select best, but only accept if we can reserve it.
    Vector3 pos = transform.position;

    // Sort candidates by score descending, then attempt to reserve in that order.
    candidateCells.Sort((a, b) =>
    {
        float da = Vector3.SqrMagnitude(dt.GridToWorldPosition(a) - pos);
        float db = Vector3.SqrMagnitude(dt.GridToWorldPosition(b) - pos);
        // Add stable-ish jitter
        da += (float)_rng.NextDouble() * 0.2f;
        db += (float)_rng.NextDouble() * 0.2f;
        return db.CompareTo(da);
    });

    for (int i = 0; i < candidateCells.Count; i++)
    {
        var cell = candidateCells[i];
        if (!TryReserveCell(cell)) continue; // contention-safe

        worldCenter = dt.GridToWorldPosition(cell);
        return true;
    }

    return false;
}

    private IEnumerator MovementRoutine()
{
    // NOTE: This routine is intentionally "grid-intent + weather" rather than "rush to ribbon".
    // - If a desired SpawnGrid target is set (from InstrumentTrack), we seek that cell center.
    // - Once we reach it (within arriveRadius), we stop seeking and let weather/flow dominate.
    // - We avoid jitter by snapping when a step would overshoot, and by latching arrival.

    var gfm = GameFlowManager.Instance;
    var dt  = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null;
    if (!_rb && !TryGetComponent(out _rb)) yield break;

    _speed = ComputeMoveSpeed();
    float linger = ComputeLingerSeconds();

    // Initial target:
    if (_hasDesiredGridTarget && dt != null)
    {
        _targetWorld = dt.GridToWorldPosition(_desiredGridTarget);
        _hasTarget = true;
    }
    else
    {
        _hasTarget = TryPickNextGridNode(out _targetWorld);
    }

    float nextChooseAt = 0f;

    // Tiny weather coupling (keeps motion feeling "in the event" without overpowering the chase).
    const float FLOW_STEP_SCALE = 0.22f;      // how much flow influences movement step
    const float TURB_STEP_SCALE = 0.12f;      // low-frequency wobble
    const float ARRIVE_LATCH_SECONDS = 0.10f; // prevents bounce by holding "arrived" briefly

    // Stable turbulence seed
    _rng ??= new System.Random(StableSeed());
    float seedA = (float)_rng.NextDouble() * 1000f;
    float seedB = (float)_rng.NextDouble() * 1000f;

    float arrivedLatchUntil = 0f;

    while (true)
    {
        yield return new WaitForFixedUpdate();

            if (_dustEscapeCooldown > 0f) _dustEscapeCooldown -= Time.fixedDeltaTime;

        if (_rb == null) continue;

        var gfm2 = GameFlowManager.Instance;
        var dustGen = gfm2 != null ? gfm2.dustGenerator : null;
        var dt2 = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null;

        Vector2 cur = _rb.position;

        // If we are inside dust, shove toward open space aggressively.
        // This prevents collectables from "drifting across dust" and supports the labyrinth readability.
        if (IsPositionInsideDust(cur))
        {
            // Probe 8 directions and pick the first clear direction; fall back to the "least bad".
            Vector2[] dirs = {
                Vector2.up, Vector2.down, Vector2.left, Vector2.right,
                (Vector2.up + Vector2.left).normalized,
                (Vector2.up + Vector2.right).normalized,
                (Vector2.down + Vector2.left).normalized,
                (Vector2.down + Vector2.right).normalized
            };

            Vector2 best = Vector2.up;
            float bestScore = float.NegativeInfinity;
            float probe = Mathf.Max(0.12f, dustAdjacencyProbe);

            for (int i = 0; i < dirs.Length; i++)
            {
                Vector2 p = cur + dirs[i] * probe;
                bool inside = IsPositionInsideDust(p);
                float score = inside ? -1f : 1f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = dirs[i];
                    if (!inside) break; // early out: found open space
                }
            }

            // Impulse-like "pop" out of dust.
            // Gate this so we do not jitter back/forth at the dust edge.
            if (_dustEscapeCooldown <= 0f)
            {
                _rb.MovePosition(cur + best * Mathf.Max(0.10f, _speed * Time.fixedDeltaTime));
                _dustEscapeCooldown = dustEscapeCooldownSeconds;
            }
            _hasTarget = false;
            arrivedLatchUntil = Time.time + ARRIVE_LATCH_SECONDS;
            continue;
        }

        // Update desired target world position from SpawnGrid target (if provided).
        if (_hasDesiredGridTarget && dt2 != null)
        {
            _targetWorld = dt2.GridToWorldPosition(_desiredGridTarget);
            _hasTarget = true;
        }
        else if (!_hasTarget)
        {
            if (Time.time < arrivedLatchUntil) continue;

            // Reacquire wandering target when not seeking.
            if (!TryPickNextGridNode(out _targetWorld))
            {
                continue;
            }
            _hasTarget = true;
        }

        Vector2 delta = (Vector2)(_targetWorld - (Vector3)cur);
        float dist = delta.magnitude;

        // Arrival handling: snap and (if desired-target) release intent so weather can take over.
        if (dist <= Mathf.Max(0.0001f, arriveRadius))
        {
            _rb.MovePosition((Vector2)_targetWorld);

            if (_hasDesiredGridTarget)
            {
                // We reached the desired SpawnGrid cell. Stop "seeking" to avoid bopping.
                _hasDesiredGridTarget = false;
            }

            _hasTarget = false;
            arrivedLatchUntil = Time.time + Mathf.Max(ARRIVE_LATCH_SECONDS, linger);
            continue;
        }

        // Compute baseline step toward target.
        Vector2 stepDir = delta / Mathf.Max(0.0001f, dist);
        Vector2 step = stepDir * (_speed * Time.fixedDeltaTime);

        // Weather influence: flow + coherent turbulence.
        if (dustGen != null)
        {
            Vector2 flow = dustGen.SampleFlowAtWorld((Vector3)cur);
            if (flow.sqrMagnitude > 0.0001f)
            {
                step += flow.normalized * (FLOW_STEP_SCALE * _speed * Time.fixedDeltaTime);
            }
        }

        float t = Time.time;
        float nx = Mathf.PerlinNoise(seedA, t * 0.35f) * 2f - 1f;
        float ny = Mathf.PerlinNoise(seedB, t * 0.35f) * 2f - 1f;
        Vector2 turb = new Vector2(nx, ny);
        if (turb.sqrMagnitude > 0.0001f)
        {
            step += turb.normalized * (TURB_STEP_SCALE * _speed * Time.fixedDeltaTime);
        }

        // Overshoot protection: if this step would pass the target, snap to target.
        if (step.magnitude >= dist)
        {
            _rb.MovePosition((Vector2)_targetWorld);

            if (_hasDesiredGridTarget)
            {
                _hasDesiredGridTarget = false;
            }

            _hasTarget = false;
            arrivedLatchUntil = Time.time + Mathf.Max(ARRIVE_LATCH_SECONDS, linger);
            continue;
        }

        _rb.MovePosition(cur + step);

        // If we are seeking a desired cell, do NOT mid-course hop (that is the source of "jerk between 2 cells").
        if (_hasDesiredGridTarget) continue;

        // Periodically re-evaluate neighbors to keep motion lively when wandering.
        if (Time.time >= nextChooseAt)
        {
            nextChooseAt = Time.time + chooseNextEvery;

            float d01 = Duration01();
            bool allowMidcourse = _rng.NextDouble() < Mathf.Lerp(0.35f, 0.05f, d01); // short notes more midcourse changes
            if (allowMidcourse && TryPickNextGridNode(out var mid))
            {
                _targetWorld = mid;
                _hasTarget = true;
            }
        }
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
        _rng ??= new System.Random(StableSeed());
        StartCoroutine(MovementRoutine());
        // --- Occupancy initialization (Mode A: never overlap another collectable) ---
        ClearReservation();

        var dt = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null;
        if (dt != null)
        {
            // Prefer CellOf if present; otherwise your existing WorldToGridPosition is fine.
            _currentCell = dt.CellOf(transform.position);
            RegisterOccupant(_currentCell);
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
    public void TravelAlongTetherAndFinalize(int durationTicks, float force, float seconds = 0.35f)
    {
        StartCoroutine(TravelRoutine(durationTicks, force, seconds));
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
    private IEnumerator TravelRoutine(int durationTicks, float force, float seconds)
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

        var ml = ribbonMarker ? ribbonMarker.GetComponent<MarkerLight>() : null;
        if (ml) ml.LightUp(tether != null ? tether.baseColor : Color.white);

        ReportedCollected = true; 
        OnCollected?.Invoke(durationTicks, force); // ✅ event raised inside Collectable
        OnDestroyed?.Invoke();
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
    }

    private void OnDestroy()
    {
        ClearReservation();
        UnregisterOccupant();
        NotifyDestroyedOnce();
    }

    private void OnDisable()
    {
        ClearReservation();
        UnregisterOccupant();
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

private void OnTriggerEnter2D(Collider2D coll)
    {
        var vehicle = coll.GetComponent<Vehicle>();
        if (vehicle == null || _handled) return;
        _handled = true; // ✅ idempotent

        // (Optional availability check)
        // if (_availableUntilDsp > 0 && AudioSettings.dspTime > _availableUntilDsp) { OnFailedCollect(); return; }

        // 1) compute loop timing
        var drumTrack    = assignedInstrumentTrack.drumTrack;
        double dspNow    = AudioSettings.dspTime;
        double loopLen   = drumTrack.GetLoopLengthInSeconds();
        double stepDur   = loopLen / drumTrack.totalSteps;
        double timingWin = stepDur * drumTrack.timingWindowSteps * 0.5;
        double loopStart = drumTrack.startDspTime;
        double tPos      = (dspNow - loopStart) % loopLen;
        if (tPos < 0) tPos += loopLen;

        // 2) pick the best matching target step (within timing window)
        int matchedStep = -1;
        double bestErr = timingWin;
        for (int i = 0; i < sharedTargetSteps.Count; i++)
        {
            int step = sharedTargetSteps[i];
            double stepPos = (step * stepDur) % loopLen;
            double delta   = Math.Abs(stepPos - tPos);
            if (delta > loopLen * 0.5) delta = loopLen - delta;

            if (delta < bestErr)
            {
                bestErr = delta;
                matchedStep = step;
            }
        }

            // success path
            float force = vehicle.GetForceAsMidiVelocity();
            vehicle.CollectEnergy(amount);
            Debug.Log("[COLLECT] Collectable Triggered");
            assignedInstrumentTrack.OnCollectableCollected(this, intendedStep >= 0 ? intendedStep : matchedStep, noteDurationTicks, force);
            if (TryGetComponent(out Collider2D col)) col.enabled = false;
            var explode = GetComponent<Explode>();
            if(explode != null) explode.Permanent(false);

    }
}
