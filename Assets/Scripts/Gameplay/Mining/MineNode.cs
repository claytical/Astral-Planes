using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

public enum MineNodeState { Drifting, Fleeing }

public class MineNode : MonoBehaviour
{
    public SpriteRenderer coreSprite;
    public SpriteRenderer outlineSprite;
    public int maxStrength = 100;

    [Header("Dust Affinity")]
    [SerializeField, Range(0f, 1f)] private float sameRoleDustAffinityStrength = 0.25f;
    [SerializeField, Min(1)] private int sameRoleDustAffinityRadiusCells = 2;
    public bool pruneCarvedPathOnLoopBoundary = true;
    [Tooltip("Minimum carved path length before pruning runs.")]
    [Min(2)] public int pruneMinPathCount = 8;

    [Header("State Machine")]
    [SerializeField] private float driftSpeedMultiplier = 0.35f;

    [Header("Flee Boundary Targeting")]
    [Tooltip("How strongly fleeing node steers toward the nearest horizontal screen edge.")]
    [SerializeField, Range(0f, 1f)] private float fleeTowardBoundaryWeight = 0.45f;

    [Header("NoteSet-Driven Motion")]
    [SerializeField] private bool driveCarvingMotionFromNoteSet = true;

    [Header("Expiry")]
    [Tooltip("Number of loop boundaries after spawn before the node expires if not captured. 0 = never.")]
    [SerializeField, Min(0)] private int expireAfterLoops = 3;

    public event System.Action<MineNode> OnResolved;

    public bool WasCaptured { get; private set; }
    public bool WasEscaped  { get; private set; }
    public MineNodeState State { get; private set; } = MineNodeState.Drifting;
    public DrumTrack DrumTrack => _drumTrack;

    private Vector2Int _spawnCell;
    private bool _hasSpawnCell;
    private HashSet<int> _turnStepSet;
    private NoteSet _cachedTurnSetSource;
    private float _nextEscapeAllowedTime = 0f;
    private Vector2 _lastPosForStall;
    private float _nextStallSampleTime = 0f;
    private int _stallHits = 0;
    private int _stepsPerLoop = 16;
    private int _strength;
    private Vector3 _originalScale;
    private Color _lockedColor;
    private bool _depletedHandled, _resolvedFired;
    private int _loopsSinceSpawn;
    private Collider2D _col;
    private Rigidbody2D _rb;
    private float _lastNote01 = 0.5f;
    private DrumTrack _drumTrack;
    private readonly List<Vector2Int> _carvedPath = new List<Vector2Int>();
    private NoteSet _noteSet;
    private InstrumentTrack _track;
    private MusicalRole _role;
    private float _speed    = 0.5f;
    private int _lastProcessedStep = -1;
    private float _rescanTimer = 0f;
    private Vector2 _carveDir = Vector2.right;
    private MineNodeDustInteractor _dustInteractor;
    private Camera _cam;
    private float _currentDesiredSpeed;
    private bool _hasBeenStruck;

    // Motion tunables (previously local consts in FixedUpdate)
    private const float kStallSpeed        = 0.20f;
    private const float kStuckDot          = 0.10f;
    private const float kEscapeJitterDeg   = 25f;
    private const float kMinSpeedFloor     = 0.25f;
    private const float kStallSamplePeriod = 0.40f;
    private const float kStallDistanceEps  = 0.12f;
    private const float kEscapeCooldown    = 0.30f;

    public MusicalRole GetImprintRole() => _role;

    public void Initialize(InstrumentTrack track, NoteSet noteSet, Color tint, Vector2Int spawnCell,
                           Sprite diamondSprite = null)
    {
        _track = track;
        _spawnCell = spawnCell;
        _role = track != null ? track.assignedRole : default;
        _noteSet = noteSet;
        _lockedColor = tint;
        _drumTrack = (track != null) ? track.drumTrack : null;
        var prof = MusicalRoleProfileLibrary.GetProfile(_role);
        if (prof != null)
        {
            _speed   = prof.mineNodeSpeed;
        }
        var explode = GetComponent<Explode>();
        if (explode != null) explode.SetTint(_lockedColor, multiply: true);

        float a = UnityEngine.Random.Range(0f, 360f);
        _carveDir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)).normalized;
        _lastProcessedStep = -1;
        if (_drumTrack != null)
        {
            _drumTrack.RegisterMineNode(this);
            _drumTrack.OnLoopBoundary += HandleLoopBoundary;
        }

        var dust = GetComponent<MineNodeDustInteractor>();
        if (dust != null) dust.SetLevelAuthority(_drumTrack);
        _carvedPath.Clear();
        if (coreSprite != null)
        {
            tint.a = 1;
            coreSprite.color = tint;
            if (diamondSprite != null) coreSprite.sprite = diamondSprite;
        }
        if (outlineSprite != null)
        {
            Color outlineTint = tint;
            outlineTint.a = 1f;
            outlineSprite.color = outlineTint;
        }
        CacheAuthoredStepsFromNoteSet();
    }

    private void HandleLoopBoundary()
    {
        if (_drumTrack == null) return;

        if (!_depletedHandled)
            _loopsSinceSpawn++;

        if (!pruneCarvedPathOnLoopBoundary) return;
        if (_carvedPath == null || _carvedPath.Count < pruneMinPathCount) return;
        int before = _carvedPath.Count;
        var compact = new List<Vector2Int>(before);
        Vector2Int? last = null;
        for (int i = 0; i < _carvedPath.Count; i++)
        {
            var cell = _carvedPath[i];
            if (_drumTrack.HasDustAt(cell)) continue;
            if (last.HasValue && last.Value == cell) continue;
            compact.Add(cell);
            last = cell;
        }
        if (compact.Count == before) return;
        _carvedPath.Clear();
        _carvedPath.AddRange(compact);
    }

    private void Start()
    {
        _rb = GetComponent<Rigidbody2D>();
        _dustInteractor = GetComponent<MineNodeDustInteractor>();
        _originalScale = transform.localScale;
        _strength = maxStrength;
    }

    private void Update()
    {
        if (coreSprite != null)
        {
            var c = coreSprite.color;
            if (!Mathf.Approximately(c.a, 1f))
            {
                c.a = 1f;
                coreSprite.color = c;
            }
        }
    }

    // ---------------------------------------------------------------
    // State machine
    // ---------------------------------------------------------------

    private void TransitionToFleeing()
    {
        if (_hasBeenStruck) return;
        _hasBeenStruck = true;
        State = MineNodeState.Fleeing;
        Debug.Log($"[MineNode] {name} — transitioning to Fleeing.");
    }

    public void HandleEscape()
    {
        if (_depletedHandled || _resolvedFired) return;
        WasEscaped = true;
        Debug.Log($"[MineNode] {name} — escaped through boundary.");
        FireResolvedOnce();
        StartCoroutine(CleanupAndDestroy());
    }

    // ---------------------------------------------------------------
    // FixedUpdate — state dispatcher
    // ---------------------------------------------------------------

    private void FixedUpdate()
    {
        if (!driveCarvingMotionFromNoteSet ||
            _rb == null ||
            _drumTrack == null ||
            _noteSet == null ||
            _track == null)
            return;

        switch (State)
        {
            case MineNodeState.Drifting: FixedUpdateDrifting(); break;
            case MineNodeState.Fleeing:  FixedUpdateFleeing();  break;
        }
    }

    private void FixedUpdateDrifting()
    {
        Vector2Int myCell = _drumTrack.WorldToGridPosition(_rb.position);
        RunCorridorLookahead(myCell);

        float targetSpeed = Mathf.Max(Mathf.Lerp(0.5f, 2.5f, _speed) * driftSpeedMultiplier, kMinSpeedFloor);
        _currentDesiredSpeed = targetSpeed;

        if (_dustInteractor == null) _dustInteractor = GetComponent<MineNodeDustInteractor>();
        _dustInteractor?.SetDesiredSpeed(targetSpeed);

        float steer    = Mathf.Lerp(3f,  20f, _speed);
        float maxSteer = Mathf.Lerp(15f, 80f, _speed);
        Vector2 force  = Vector2.ClampMagnitude((_carveDir * targetSpeed - _rb.linearVelocity) * steer * _rb.mass, maxSteer);
        _rb.AddForce(force, ForceMode2D.Force);

        RunStallEscape(myCell);
        RunBoundaryClamp(true, true);
    }

    private void FixedUpdateFleeing()
    {
        // Off-screen escape — fallback for when BoundaryWrap trigger is missed at high velocity.
        if (_cam == null) _cam = Camera.main;
        if (_cam != null)
        {
            const float kEscapeMargin = 2f;
            float halfW = _cam.orthographicSize * _cam.aspect;
            float halfH = _cam.orthographicSize;
            Vector2 pos = _rb.position;
            if (pos.x < -halfW - kEscapeMargin || pos.x > halfW + kEscapeMargin ||
                pos.y < -halfH - kEscapeMargin || pos.y > halfH + kEscapeMargin)
            {
                HandleEscape();
                return;
            }
        }

        Vector2Int myCell = _drumTrack.WorldToGridPosition(_rb.position);
        RunCorridorLookahead(myCell);

        int stepNow     = _drumTrack.currentStep;
        int currentNote = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);
        float speed01   = Mathf.InverseLerp(_track.lowestAllowedNote, _track.highestAllowedNote, currentNote);
        _lastNote01 = speed01;

        float speedMin = Mathf.Lerp(0.5f,  2.5f, _speed);
        float speedMax = Mathf.Lerp(3f,   14f,  _speed);
        float steer    = Mathf.Lerp(3f,   20f,  _speed);
        float maxSteer = Mathf.Lerp(15f,  80f,  _speed);
        float targetSpeed = Mathf.Max(Mathf.Lerp(speedMin, speedMax, speed01), kMinSpeedFloor);
        _currentDesiredSpeed = targetSpeed;

        if (_dustInteractor == null) _dustInteractor = GetComponent<MineNodeDustInteractor>();
        _dustInteractor?.SetDesiredSpeed(targetSpeed);

        // Steer toward nearest horizontal screen edge
        if (_cam == null) _cam = Camera.main;
        if (_cam != null)
        {
            float camHalfW  = _cam.orthographicSize * _cam.aspect;
            float leftDist  = _rb.position.x - (-camHalfW);
            float rightDist = camHalfW - _rb.position.x;
            float targetX   = leftDist < rightDist ? -camHalfW : camHalfW;
            Vector2 toEdge  = new Vector2(targetX - _rb.position.x, 0f).normalized;
            if (toEdge.sqrMagnitude > 0.0001f)
            {
                // Only bias toward the edge when that direction is not blocked by dust.
                bool edgeBlocked = false;
                for (int i = 1; i <= 2; i++)
                {
                    var probe = myCell + new Vector2Int(Mathf.RoundToInt(toEdge.x * i), Mathf.RoundToInt(toEdge.y * i));
                    if (_drumTrack.HasDustAt(probe)) { edgeBlocked = true; break; }
                }
                if (!edgeBlocked)
                    _carveDir = Vector2.Lerp(_carveDir, toEdge, fleeTowardBoundaryWeight).normalized;
            }
        }

        Vector2 force = Vector2.ClampMagnitude((_carveDir * targetSpeed - _rb.linearVelocity) * steer * _rb.mass, maxSteer);
        _rb.AddForce(force, ForceMode2D.Force);

        RunStallEscape(myCell);
        RunBoundaryClamp(false, true); // No X clamp — let it reach the side edge
    }

    // ---------------------------------------------------------------
    // Shared motion helpers
    // ---------------------------------------------------------------

    private void RunCorridorLookahead(Vector2Int myCell)
    {
        _rescanTimer -= Time.fixedDeltaTime;

        bool wallAhead = false;
        for (int i = 1; i <= 3; i++)
        {
            var probe = myCell + new Vector2Int(Mathf.RoundToInt(_carveDir.x * i), Mathf.RoundToInt(_carveDir.y * i));
            if (_drumTrack.HasDustAt(probe)) { wallAhead = true; break; }
        }

        if (!wallAhead)
        {
            Vector2 velN = _rb.linearVelocity.sqrMagnitude > 0.04f ? _rb.linearVelocity.normalized : Vector2.zero;
            if (velN.sqrMagnitude > 0.0001f)
            {
                for (int i = 1; i <= 2; i++)
                {
                    var probe = myCell + new Vector2Int(Mathf.RoundToInt(velN.x * i), Mathf.RoundToInt(velN.y * i));
                    if (_drumTrack.HasDustAt(probe)) { wallAhead = true; break; }
                }
            }
        }

        if (wallAhead || _rescanTimer <= 0f)
        {
            _rescanTimer = 0.6f;
            Vector2[] candidates =
            {
                Vector2.right,
                new Vector2( 1f,  1f).normalized,
                Vector2.up,
                new Vector2(-1f,  1f).normalized,
                Vector2.left,
                new Vector2(-1f, -1f).normalized,
                Vector2.down,
                new Vector2( 1f, -1f).normalized,
            };

            float bestScore = float.MinValue;
            Vector2 bestDir = _carveDir;
            foreach (var dir in candidates)
            {
                float score = 0f;
                for (int i = 1; i <= 3; i++)
                {
                    var probe = myCell + new Vector2Int(Mathf.RoundToInt(dir.x * i), Mathf.RoundToInt(dir.y * i));
                    if (!_drumTrack.HasDustAt(probe)) score += 1f;
                    else break;
                }
                for (int d = 1; d <= 3; d++)
                {
                    var lc = myCell + new Vector2Int(Mathf.RoundToInt(-dir.y * d), Mathf.RoundToInt( dir.x * d));
                    var rc = myCell + new Vector2Int(Mathf.RoundToInt( dir.y * d), Mathf.RoundToInt(-dir.x * d));
                    if (_drumTrack.HasDustAt(lc) || _drumTrack.HasDustAt(rc))
                    {
                        score += 1.5f / d;
                        break;
                    }
                }
                if (Vector2.Dot(dir, _carveDir) < -0.5f) score *= 0.1f;
                if (score > bestScore) { bestScore = score; bestDir = dir; }
            }
            _carveDir = bestDir.normalized;
        }
    }

    private void RunStallEscape(Vector2Int myCell)
    {
        float vMag  = _rb.linearVelocity.magnitude;
        float align = (vMag > 0.0001f) ? Vector2.Dot(_rb.linearVelocity.normalized, _carveDir) : 0f;
        bool pressedNow = (vMag < kStallSpeed) || (align < kStuckDot);

        bool confirmedStall = false;
        if (Time.time >= _nextStallSampleTime)
        {
            Vector2 pos   = _rb.position;
            float   moved = (pos - _lastPosForStall).magnitude;
            if (_nextStallSampleTime > 0f)
            {
                if (moved < kStallDistanceEps && pressedNow) _stallHits++;
                else _stallHits = Mathf.Max(0, _stallHits - 1);
                confirmedStall = (_stallHits >= 1);
            }
            _lastPosForStall     = pos;
            _nextStallSampleTime = Time.time + kStallSamplePeriod;
        }

        if (!confirmedStall || Time.time < _nextEscapeAllowedTime) return;

        _nextEscapeAllowedTime = Time.time + kEscapeCooldown;
        _stallHits = 0;

        Vector2 fwd   = (_carveDir.sqrMagnitude > 0.001f) ? _carveDir.normalized : Vector2.right;
        Vector2 left  = new Vector2(-fwd.y,  fwd.x);
        Vector2 right = new Vector2( fwd.y, -fwd.x);
        int w = _drumTrack.GetSpawnGridWidth();
        int h = _drumTrack.GetSpawnGridHeight();
        Vector2 toCenter = new Vector2(w * 0.5f - myCell.x, h * 0.5f - myCell.y);
        if (toCenter.sqrMagnitude > 0.001f) toCenter.Normalize();

        float lDot = Vector2.Dot(left,  toCenter);
        float rDot = Vector2.Dot(right, toCenter);
        _carveDir = (lDot > rDot) ? left : right;

        if (vMag > 0.05f)
        {
            Vector2 vN = _rb.linearVelocity.normalized;
            float l = Mathf.Abs(Vector2.Dot(vN, left));
            float r = Mathf.Abs(Vector2.Dot(vN, right));
            _carveDir = (l < r) ? left : right;
        }
        else
        {
            _carveDir = Rotate(fwd, UnityEngine.Random.Range(150f, 210f)).normalized;
        }

        _carveDir = Rotate(_carveDir, UnityEngine.Random.Range(-kEscapeJitterDeg, kEscapeJitterDeg)).normalized;
        _rb.linearVelocity *= 0.5f;
    }

    private void RunBoundaryClamp(bool clampX, bool clampY)
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        const float pad = 0.5f;
        Vector2 pos  = _rb.position;
        Vector2 minB = _cam.ViewportToWorldPoint(new Vector3(0f, 0f, 0f));
        Vector2 maxB = _cam.ViewportToWorldPoint(new Vector3(1f, 1f, 0f));
        bool hit = false;

        if (clampX)
        {
            if      (pos.x < minB.x + pad) { pos.x = minB.x + pad; _carveDir.x =  Mathf.Abs(_carveDir.x); hit = true; }
            else if (pos.x > maxB.x - pad) { pos.x = maxB.x - pad; _carveDir.x = -Mathf.Abs(_carveDir.x); hit = true; }
        }
        if (clampY)
        {
            if      (pos.y < minB.y + pad) { pos.y = minB.y + pad; _carveDir.y =  Mathf.Abs(_carveDir.y); hit = true; }
            else if (pos.y > maxB.y - pad) { pos.y = maxB.y - pad; _carveDir.y = -Mathf.Abs(_carveDir.y); hit = true; }
        }

        if (hit)
        {
            _rb.position = pos;
            _rb.linearVelocity = _carveDir * _rb.linearVelocity.magnitude;
        }
    }

    // ---------------------------------------------------------------
    // Dust affinity (kept for potential future use)
    // ---------------------------------------------------------------

    private Vector2 ComputeLocalSameRoleAffinityDir(int radiusCells)
    {
        if (_drumTrack == null) return Vector2.zero;
        Vector2Int center = _drumTrack.CellOf(_rb.position);
        Vector2 accum = Vector2.zero;
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            Vector2Int gp = new Vector2Int(center.x + dx, center.y + dy);
            if (!_drumTrack.TryGetDustAt(gp, out var dust) || dust == null) continue;
            if (dust.Role != _role) continue;
            Vector2 world = _drumTrack.GridToWorldPosition(gp);
            Vector2 to    = world - _rb.position;
            float dist    = to.magnitude;
            if (dist <= 0.0001f) continue;
            accum += to.normalized * (1f / (1f + dist));
        }
        return accum.sqrMagnitude > 0.0001f ? accum.normalized : Vector2.zero;
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
        int bar = 16;
        if (_stepsPerLoop % bar != 0) _stepsPerLoop = ((_stepsPerLoop / bar) + 1) * bar;
    }

    public void ReflectCarveDir(bool reflectX)
    {
        if (reflectX) _carveDir.x = -_carveDir.x;
        else          _carveDir.y = -_carveDir.y;
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
    }

    // ---------------------------------------------------------------
    // Depletion / resolution
    // ---------------------------------------------------------------

    private void HandleDepleted(Vehicle vehicle)
    {
        _depletedHandled = true;
        WasCaptured = true;

        var origin    = transform.position;
        var repelFrom = vehicle != null ? vehicle.transform.position : origin;

        Vector2 blastDir = ((Vector2)(origin - repelFrom)).sqrMagnitude > 0.0001f
            ? ((Vector2)(origin - repelFrom)).normalized
            : Vector2.up;

        var explode = GetComponent<Explode>();
        if (explode != null) explode.SetBurstDirection(blastDir);

        _track.SpawnCollectableBurst(_noteSet, -1, -1, origin, repelFrom, 1.0f, 140f, 0.18f, InstrumentTrack.BurstPlacementMode.Free, 10);
        TriggerExplosion();
    }

    private void TryPlayPreviewNote()
    {
        if (_track == null || _noteSet == null || _drumTrack == null) return;
        int stepNow = _drumTrack.currentStep;
        int note    = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);
        _track.PlayNote127(note, 200, 0.5f);
        if (GetComponent<Explode>() != null) GetComponent<Explode>().PreExplode();
    }

    private IEnumerator CleanupAndDestroy()
    {
        yield return null;
        var dt = _drumTrack;
        if (dt != null)
        {
            dt.OnLoopBoundary -= HandleLoopBoundary;
            _drumTrack.FreeSpawnCell(_spawnCell.x, _spawnCell.y);
            dt.UnregisterMineNode(this);
        }
        Destroy(gameObject);
    }

    private void TriggerExplosion()
    {
        Debug.Log($"Triggering Explosion in Mine Node");
        var explosion = GetComponent<Explode>();
        if (explosion != null) explosion.Permanent(false);
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
        if (transform.localScale.magnitude <= 0.05f) TriggerExplosion();
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (_depletedHandled) return;
        if (!coll.gameObject.TryGetComponent<Vehicle>(out var vehicle)) return;

        TryPlayPreviewNote();

        _strength -= vehicle.GetForceAsDamage();
        _strength  = Mathf.Max(0, _strength);

        TransitionToFleeing();

        float normalized  = (maxStrength > 0) ? (float)_strength / maxStrength : 0f;
        float scaleFactor = Mathf.Lerp(0.3f, 1.1f, normalized);
        StartCoroutine(ScaleSmoothly(_originalScale * scaleFactor, 0.1f));

        if (_strength <= 0) HandleDepleted(vehicle);
    }

    private void OnDisable()
    {
        Debug.Log($"[MineNode] OnDisable {name} ({GetInstanceID()})");
        if (_drumTrack != null) _drumTrack.OnLoopBoundary -= HandleLoopBoundary;
    }

    private void OnDestroy()
    {
        Debug.Log($"[MineNode] OnDestroy {name} ({GetInstanceID()})");
        if (_drumTrack != null) _drumTrack.OnLoopBoundary -= HandleLoopBoundary;
        if (_drumTrack != null) _drumTrack.activeMineNodes.Remove(this);
    }

    void OnEnable()
    {
        _col = GetComponent<Collider2D>();
        _rb  = GetComponent<Rigidbody2D>();
        if (_col != null) _col.enabled = true;
        if (_rb  != null) _rb.simulated = true;
    }
}
