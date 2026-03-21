using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

public class MineNode : MonoBehaviour
{
    public SpriteRenderer coreSprite;
    public int maxStrength = 100;

    [Header("Dust Affinity")]
    [SerializeField, Range(0f, 1f)] private float sameRoleDustAffinityStrength = 0.25f; // medium
    [SerializeField, Min(1)] private int sameRoleDustAffinityRadiusCells = 2;            // moderate
    public bool pruneCarvedPathOnLoopBoundary = true;
    [Tooltip("Minimum carved path length before pruning runs.")] 
    [Min(2)] public int pruneMinPathCount = 8;

    [Header("NoteSet-Driven Motion (Carving)")]
    [SerializeField] private bool driveCarvingMotionFromNoteSet = true;

    public event System.Action<MineNode> OnResolved;
    public DrumTrack DrumTrack => _drumTrack;

    private Vector2Int _spawnCell;
    private bool _hasSpawnCell;
    private HashSet<int> _turnStepSet;
    private NoteSet _cachedTurnSetSource;
    private float _nextEscapeAllowedTime = 0f;
    private Vector2 _lastPosForStall;
    private float _nextStallSampleTime = 0f;
    private int _stallHits = 0;
    private int _stepsPerLoop = 16;     // derived from allowed steps
    private int _strength;
    private Vector3 _originalScale;
    private Color _lockedColor;
    private bool _depletedHandled, _resolvedFired;
    private Collider2D _col;
    private Rigidbody2D _rb;
    // Cached normalized pitch (0 = lowest note, 1 = highest note)
    private float _lastNote01 = 0.5f;
    private DrumTrack _drumTrack;
    // --- Grid-based path recording (only during carving) ---
    // Each entry is a grid cell the node occupied while carving.
    private readonly List<Vector2Int> _carvedPath = new List<Vector2Int>();
    private NoteSet _noteSet;
    private InstrumentTrack _track;
    private MusicalRole _role;
    private float _speed    = 0.5f;
    private float _clearing = 0.5f;
    private float _agility  = 0.5f;
    private int _lastProcessedStep = -1;
    private Vector2 _carveDir = Vector2.right; // will be randomized on init
    private MineNodeDustInteractor _dustInteractor;
    private float _currentDesiredSpeed;
        public MusicalRole GetImprintRole()
    {
        return _role;
    }
    public void Initialize(InstrumentTrack track, NoteSet noteSet, Color tint, Vector2Int spawnCell) {
        _track = track;
        _spawnCell = spawnCell;
        _role = track != null ? track.assignedRole : default;
        _noteSet = noteSet;
        _lockedColor = tint;
        _drumTrack = (track != null) ? track.drumTrack : null;
        var prof = MusicalRoleProfileLibrary.GetProfile(_role);
        if (prof != null)
        {
            _speed    = prof.mineNodeSpeed;
            _clearing = prof.mineNodeClearing;
            _agility  = prof.mineNodeAgility;
        }
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.SetTint(_lockedColor, multiply: true);
        }
        
        float a = UnityEngine.Random.Range(0f, 360f); 
        _carveDir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)).normalized; 
        _lastProcessedStep = -1;
        if (_drumTrack != null) { 
            _drumTrack.RegisterMineNode(this); 
            _drumTrack.OnLoopBoundary += HandleLoopBoundary;
        }
        
        var dust = GetComponent<MineNodeDustInteractor>(); 
        if (dust != null) dust.SetLevelAuthority(_drumTrack); 
        _carvedPath.Clear(); 
        if (track != null) ConfigureFromRole(track.assignedRole);
        if (coreSprite != null) {
            tint.a = 1; 
            coreSprite.color = tint;
        }
        CacheAuthoredStepsFromNoteSet(); 
    }

    private void HandleLoopBoundary()
    { if (_drumTrack == null) return;
        if (!pruneCarvedPathOnLoopBoundary) return; 
        if (_carvedPath == null || _carvedPath.Count < pruneMinPathCount) return; 
        int before = _carvedPath.Count;
        // Keep only cells that are still clear (no dust present).
        // This makes _carvedPath represent the *currently-open corridor*, not historical traversal.
        var compact = new List<Vector2Int>(before);
        Vector2Int? last = null;
        for (int i = 0; i < _carvedPath.Count; i++) {
            var cell = _carvedPath[i];
            // If dust has regrown here, it is no longer part of the active corridor.
            if (_drumTrack.HasDustAt(cell)) continue;
            // Avoid duplicates from lingering in a cell.
            if (last.HasValue && last.Value == cell) continue;
            compact.Add(cell);
            last = cell;
        }
        if (compact.Count == before) return; // no changes
        _carvedPath.Clear(); 
        _carvedPath.AddRange(compact);
    }
    public float GetCorridorHealDelaySeconds()
    {
        // tunables (make them per-role later if desired)
        float slow = 16f;  // low note
        float fast = 4f;  // high note
        return Mathf.Lerp(slow, fast, _lastNote01); // note01 high => fast
    }
    public void NotifyDustErodedAt(Vector3 worldPos)
    {
        if (_drumTrack == null) return;

        // Project carving position into the grid
        Vector2Int cell = _drumTrack.CellOf(worldPos);

        // Avoid duplicate samples when staying in the same cell
        if (_carvedPath.Count > 0 && _carvedPath[_carvedPath.Count - 1] == cell)
            return;

        _carvedPath.Add(cell);
    }
    private void Start()
    {
        _rb = GetComponent<Rigidbody2D>();
        _dustInteractor = GetComponent<MineNodeDustInteractor>();
        _originalScale = transform.localScale;
        _strength = maxStrength;
    }
    private void FixedUpdate()
{
    if (!driveCarvingMotionFromNoteSet ||
        _rb == null ||
        _drumTrack == null ||
        _noteSet == null ||
        _track == null)
    {
        return;
    }

    EnsureTurnStepsCached();

    // ---- Tunables (safe defaults) ----
    const float microTurnGate        = 0.15f; // how much we turn on non-authored steps
    const float stallSpeed           = 0.20f; // below this, consider "stalled"
    const float stuckDot             = 0.10f; // below this alignment, consider "not progressing"
    const float escapeJitterDeg      = 25f;   // random jitter after an escape redirect
    const float minDesiredSpeedFloor = 0.25f; // avoid meaningless speed caps downstream

    // New: stuck breaker sampling / cooldown
    const float stallSamplePeriod    = 0.40f; // how often we evaluate true "not moving"
    const float stallDistanceEps     = 0.12f; // moved less than this in sample window => stall hit
    const float escapeCooldown       = 0.30f; // don't escape every frame

    // STEP INDEX SOURCE
    // If _drumTrack.currentStep is stable and DSP-based, keep using it:
    int stepNow = _drumTrack.currentStep;

    // --- STEP-BASED TURNING (authored-step keyframes) ---
    if (stepNow != _lastProcessedStep)
    {
        _lastProcessedStep = stepNow;

        bool isTurnStep = (_turnStepSet != null && _turnStepSet.Contains(stepNow));
        float turnGate  = isTurnStep ? 1f : microTurnGate;

        int note = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);

        float note01 = Mathf.InverseLerp(
            _track.lowestAllowedNote,
            _track.highestAllowedNote,
            note);

        _lastNote01 = note01;

        float baseAngle = GetRoleTurnAngleDeg(_role);

        // Scale turn magnitude by pitch + whether this step is authored as a turn.
        float turnAngle = Mathf.Lerp(baseAngle * 0.5f, baseAngle * 1.25f, note01) * turnGate;

        // If you want deterministic "jitter" per step, replace Random.Range with a hash of (stepNow, _rngSalt).
        float delta = UnityEngine.Random.Range(-turnAngle, turnAngle);

        _carveDir = Rotate(_carveDir, delta).normalized;
    }
    var vehicles = GameFlowManager.Instance?.localPlayers;
    if (vehicles != null)
    {
        Vector2 myPos = _rb.position;
        Vector2 flee = Vector2.zero;
        float fleeRadiusSq = 25f; // 5 units
        foreach (var lp in vehicles)
        {
            var v = lp?.plane;
            if (v == null) continue;
            Vector2 away = myPos - (Vector2)v.transform.position;
            float dSq = away.sqrMagnitude;
            if (dSq < fleeRadiusSq && dSq > 0.001f)
                flee += away / dSq; // inverse-square falloff
        }
        if (flee.sqrMagnitude > 0.001f)
            _carveDir = Vector2.Lerp(_carveDir, flee.normalized, 0.25f).normalized;
    }
    // --- NOTE-DRIVEN SPEED ---
    int currentNote = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);

    float speed01 = Mathf.InverseLerp(
        _track.lowestAllowedNote,
        _track.highestAllowedNote,
        currentNote);
    ApplyLocalDustAffinity();
    float effectiveSpeedMin = Mathf.Lerp(0.5f,  2.5f, _speed);
    float effectiveSpeedMax = Mathf.Lerp(3f,   14f,   _speed);
    float effectiveSteer    = Mathf.Lerp(3f,   20f,   _speed);
    float effectiveMaxSteer = Mathf.Lerp(15f,  80f,   _speed);
    float targetSpeed = Mathf.Lerp(effectiveSpeedMin, effectiveSpeedMax, speed01);
    targetSpeed = Mathf.Max(targetSpeed, minDesiredSpeedFloor);

    if (_dustInteractor == null) _dustInteractor = GetComponent<MineNodeDustInteractor>();
    if (_dustInteractor != null) _dustInteractor.SetDesiredSpeed(targetSpeed);

    // --- CONTINUOUS STEERING FORCE ---
    Vector2 desiredVelocity = _carveDir * targetSpeed;
    Vector2 velocityDelta   = desiredVelocity - _rb.linearVelocity;

    // Use mass-scaled force. Clamp helps avoid “solver pinning” at high deltas.
    Vector2 force = velocityDelta * effectiveSteer * _rb.mass;
    force = Vector2.ClampMagnitude(force, effectiveMaxSteer);
    _rb.AddForce(force, ForceMode2D.Force);

    // --- BOUNDARY / STALL ESCAPE ---
    // Two-stage detection:
    // A) instantaneous "pressed" signal (low speed OR poor alignment)
    // B) confirmed stall via distance sampling, with cooldown to prevent ping-pong
    float vMag = _rb.linearVelocity.magnitude;

    float align = (vMag > 0.0001f)
        ? Vector2.Dot(_rb.linearVelocity.normalized, _carveDir)
        : 0f;

    bool pressedNow = (vMag < stallSpeed) || (align < stuckDot);

    // Confirm stall based on distance moved over time (prevents false triggers on valid turns)
    bool confirmedStall = false;
    if (Time.time >= _nextStallSampleTime)
    {
        Vector2 pos = _rb.position;
        float moved = (pos - _lastPosForStall).magnitude;

        if (_nextStallSampleTime > 0f) // skip the first sample
        {
            if (moved < stallDistanceEps && pressedNow) _stallHits++;
            else _stallHits = Mathf.Max(0, _stallHits - 1);

            confirmedStall = (_stallHits >= 2); // require persistence across samples
        }

        _lastPosForStall = pos;
        _nextStallSampleTime = Time.time + stallSamplePeriod;
    }

    if (confirmedStall && Time.time >= _nextEscapeAllowedTime)
    {
        _nextEscapeAllowedTime = Time.time + escapeCooldown;
        _stallHits = 0;

        // Prefer an orthogonal “escape” turn rather than a reflect guess off velocity,
        // because reflect can ping-pong on boundaries.
        // Choose left vs right based on which is more different from our current motion.
        Vector2 fwd = (_carveDir.sqrMagnitude > 0.001f) ? _carveDir.normalized : Vector2.right;
        Vector2 left  = new Vector2(-fwd.y, fwd.x);
        Vector2 right = new Vector2(fwd.y, -fwd.x);
        Vector2Int myCell = _drumTrack.WorldToGridPosition(_rb.position);
        int w = _drumTrack.GetSpawnGridWidth();
        int h = _drumTrack.GetSpawnGridHeight();
        Vector2 toCenter = new Vector2(w * 0.5f - myCell.x, h * 0.5f - myCell.y);
        if (toCenter.sqrMagnitude > 0.001f) toCenter.Normalize();

        float lDot = Vector2.Dot(left, toCenter);
        float rDot = Vector2.Dot(right, toCenter);
        _carveDir = (lDot > rDot) ? left : right;
        // If we have velocity, choose the side that reduces alignment with the blocked motion
        if (vMag > 0.05f)
        {
            Vector2 vN = _rb.linearVelocity.normalized;
            float l = Mathf.Abs(Vector2.Dot(vN, left));
            float r = Mathf.Abs(Vector2.Dot(vN, right));
            _carveDir = (l < r) ? left : right; // pick more orthogonal to current velocity
        }
        else
        {
            // Nearly stopped: pick a decisive 150–210 degree turn away from current
            _carveDir = Rotate(fwd, UnityEngine.Random.Range(150f, 210f)).normalized;
        }

        // Add jitter so it doesn't get trapped in a repeating two-turn pattern.
        _carveDir = Rotate(_carveDir, UnityEngine.Random.Range(-escapeJitterDeg, escapeJitterDeg)).normalized;

        // Dampen velocity to help separate from the boundary in the next solver step.
        _rb.linearVelocity *= 0.5f;
    }
}
    private Vector2 ComputeLocalSameRoleAffinityDir(int radiusCells)
    {
        if (_drumTrack == null) return Vector2.zero;

        Vector2Int center = _drumTrack.CellOf(_rb.position);
        Vector2 accum = Vector2.zero;

        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            for (int dy = -radiusCells; dy <= radiusCells; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                Vector2Int gp = new Vector2Int(center.x + dx, center.y + dy);

                if (!_drumTrack.TryGetDustAt(gp, out var dust) || dust == null)
                    continue;

                if (dust.Role != _role)
                    continue;

                Vector2 world = _drumTrack.GridToWorldPosition(gp);
                Vector2 to = world - _rb.position;
                float dist = to.magnitude;
                if (dist <= 0.0001f) continue;

                // same-role only, inverse-distance weighted
                float weight = 1f / (1f + dist);
                accum += to.normalized * weight;
            }
        }

        return accum.sqrMagnitude > 0.0001f ? accum.normalized : Vector2.zero;
    }
    private void ApplyLocalDustAffinity()
    {
        if (sameRoleDustAffinityStrength <= 0f) return;

        Vector2 affinityDir = ComputeLocalSameRoleAffinityDir(
            Mathf.Max(1, sameRoleDustAffinityRadiusCells));

        if (affinityDir.sqrMagnitude <= 0.0001f)
            return;

        float authoredWeight = 1f - sameRoleDustAffinityStrength;
        _carveDir = (authoredWeight * _carveDir + sameRoleDustAffinityStrength * affinityDir).normalized;
    }
    private void EnsureTurnStepsCached()
{
    if (_noteSet == null) return;
    if (_turnStepSet != null && ReferenceEquals(_cachedTurnSetSource, _noteSet)) return;

    var stepList = _noteSet.GetStepList();
    _turnStepSet = (stepList != null && stepList.Count > 0) ? new HashSet<int>(stepList) : null;
    _cachedTurnSetSource = _noteSet;
}
    private void CacheAuthoredStepsFromNoteSet()
{
    _stepsPerLoop = 16;

    if (_drumTrack == null || _noteSet == null) return;

    var steps = _noteSet.GetStepList();
    if (steps == null || steps.Count == 0) return;
    
    int max = -1;
    for (int i = 0; i < steps.Count; i++) max = Mathf.Max(max, steps[i]);
    _stepsPerLoop = Mathf.Max(1, max + 1);

    // Optional: round up to 16-step bars to keep indexing stable if you care about bars.
    // This avoids weirdness if the highest authored step isn't at the end of the loop.
    int bar = 16;
    if (_stepsPerLoop % bar != 0) _stepsPerLoop = ((_stepsPerLoop / bar) + 1) * bar;
}
    private float GetRoleTurnAngleDeg(MusicalRole _) => Mathf.Lerp(5f, 45f, _agility);
    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
    }
    
    private void ConfigureFromRole(MusicalRole role)
    {
        var dust = GetComponent<MineNodeDustInteractor>();
        if (!dust) return;
        float interval = Mathf.Lerp(0.14f, 0.04f, _clearing);
        float appetite = Mathf.Lerp(0.6f,  1.6f,  _clearing);
        dust.ConfigureCarving(interval, appetite);
    }
    private void HandleDepleted(Vehicle vehicle)
    {
        _depletedHandled = true;

        // burst spawn: your existing call site is good; just make it depend on (_track, _noteSet)
        var origin = transform.position;
        var repelFrom = vehicle != null ? vehicle.transform.position : origin;
        _track.SpawnCollectableBurst(_noteSet, 8, -1, origin, repelFrom, 4.0f, 140f, 0.18f, InstrumentTrack.BurstPlacementMode.Free, 10);

        TriggerExplosion();
    }
    private void TryPlayPreviewNote()
    {
        if (_track == null || _noteSet == null || _drumTrack == null) return;

        int stepNow = _drumTrack.currentStep;
        int note = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);
        _track.PlayNote127(note, 200, 0.5f);
    }
    private IEnumerator CleanupAndDestroy()
    {
        yield return null;

        var dt = _drumTrack;
        if (dt != null)
        {
            dt.OnLoopBoundary -= HandleLoopBoundary;
            _drumTrack.FreeSpawnCell(_spawnCell.x, _spawnCell.y); 
            dt.UnregisterMineNode(this); // if you have it
        }
        Destroy(gameObject);
    }
    private void TriggerExplosion()
    {
        Debug.Log($"Triggering Explosion in Mine Node");
        var explosion = GetComponent<Explode>();
        if(explosion != null) explosion.Permanent(false);
        
        // 🔔 Notify listeners (PhaseStar) of the outcome kind and payload
        FireResolvedOnce();
        StartCoroutine(CleanupAndDestroy());
    }
    private void FireResolvedOnce()
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        OnResolved?.Invoke(this);
    }

    private IEnumerator ScaleSmoothly(Vector3 targetScale, float duration)
    {
        Vector3 initialScale = transform.localScale;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            transform.localScale = Vector3.Lerp(initialScale, targetScale, elapsed / duration);
            elapsed += Time.deltaTime;
            yield return null;
        }

        transform.localScale = targetScale;

        if (transform.localScale.magnitude <= 0.05f)
            
            TriggerExplosion();
    } 
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (_depletedHandled) return;
        if (!coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
            return;

        // Preview note (optional)
        TryPlayPreviewNote();

        // Apply damage
        _strength -= vehicle.GetForceAsDamage();
        _strength = Mathf.Max(0, _strength);

        float normalized = (maxStrength > 0) ? (float)_strength / maxStrength : 0f;
        float scaleFactor = Mathf.Lerp(0.2f, 1.1f, normalized);
        StartCoroutine(ScaleSmoothly(_originalScale * scaleFactor, 0.1f));
        // Deplete -> burst -> resolve
        if(_strength <= 0)
            HandleDepleted(vehicle);
    }

    private void OnDisable() { 
        Debug.Log($"[MineNode] OnDisable {name} ({GetInstanceID()})"); 
        // Defensive: if we're disabled without CleanupAndDestroy running, unsubscribe here.
        if (_drumTrack != null) _drumTrack.OnLoopBoundary -= HandleLoopBoundary;
    }
    private void OnDestroy() {
        Debug.Log($"[MineNode] OnDestroy {name} ({GetInstanceID()})"); 
        // Defensive: ensure no stale event subscriptions survive destruction.
        if (_drumTrack != null) _drumTrack.OnLoopBoundary -= HandleLoopBoundary;
        // If we were destroyed unexpectedly, also try to remove from active list.
        if (_drumTrack != null) _drumTrack.activeMineNodes.Remove(this);
    }
    void OnEnable() { 
         
        _col = GetComponent<Collider2D>(); 
        _rb  = GetComponent<Rigidbody2D>(); 
        if (_col != null) _col.enabled = true; // ✅ ensure interactable
        if (_rb  != null) _rb.simulated = true;
        }
}

