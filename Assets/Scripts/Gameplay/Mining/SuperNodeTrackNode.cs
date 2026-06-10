using UnityEngine;

[DisallowMultipleComponent]
public class SuperNodeTrackNode : MonoBehaviour
{
    [SerializeField] private SpriteRenderer _renderer;
    [SerializeField] private Explode        _explode;

    [Header("Speed (difficulty-scaled)")]
    [SerializeField] private float minSpeed = 1.5f;
    [SerializeField] private float maxSpeed = 4.5f;

    [Header("Lifetime (difficulty-scaled)")]
    [SerializeField] private int maxLifetimeLoops = 5;
    [SerializeField] private int minLifetimeLoops = 1;

    [Header("Direction")]
    [SerializeField] private float reactionDelayMin   = 0.12f;
    [SerializeField] private float reactionDelayMax   = 0.35f;
    [SerializeField] private float pathCommitDuration = 0.7f;
    [SerializeField] private float continuityBias     = 0.6f;
    [SerializeField] private float turnJitterDeg      = 14f;

    [Header("Burst-Pause")]
    [SerializeField] private float burstDuration         = 0.55f;
    [SerializeField] private float pauseDuration         = 0.30f;
    [SerializeField] private float burstSpeedMultiplier  = 1.8f;
    [SerializeField] private float pauseSpeedMultiplier  = 0.55f;

    [Header("Hesitation")]
    [SerializeField] private float turnRate   = 7f;
    [SerializeField] private float hesitation = 0.12f;

    [Header("Dust Attraction")]
    [SerializeField] private float lookaheadDistance  = 1.5f;
    [SerializeField] private float wallHugBonus       = 1.0f;
    [SerializeField] private float sideCheckDistance  = 1.5f;

    [Header("Stall")]
    [SerializeField] private float stallCheckInterval  = 0.5f;
    [SerializeField] private float stallMoveThreshold  = 0.15f;

    [Header("Flee")]
    [SerializeField] private float fleeRadius          = 3.5f;
    [SerializeField] private float fleeSpeedMultiplier = 1.5f;

    [Header("Collection")]
    [SerializeField] private float spawnGraceSeconds  = 0.2f;
    [SerializeField] private int   ascendLoopsOverride = 3;

    [Header("Dust Carving")]
    [SerializeField] private float dustFadeSeconds = 0.15f;
    [SerializeField] private float dustRegrowDelay = 0.5f;

    [Header("Visual")]
    [SerializeField] private float         faceTurnRate  = 420f;
    [SerializeField] private float         wobbleDeg     = 12f;
    [SerializeField] private Vector2       wobbleHzRange = new Vector2(2.5f, 4.5f);
    [SerializeField] private ParticleSystem _particles;

    public InstrumentTrack AssignedTrack { get; private set; }
    public System.Action<bool> OnResolved;

    private float _spawnTime;
    private bool  _collected;
    private bool  _resolved;
    private int   _loopsLived;
    private int   _lifetimeLoops;
    private float _speed;

    private Rigidbody2D         _rb;
    private DrumTrack           _drum;
    private CosmicDustGenerator _dustGen;

    private Vector2 _carveDir;
    private float   _nextScanAt;
    private float   _pathCommitUntil;

    private bool  _isInBurst;
    private float _burstTimer;

    private Vector2 _lastStallCheckPos;
    private float   _nextStallCheckAt;
    private int     _stallHits;

    private float _facedAngle;
    private float _wobblePhase;

    private static readonly RaycastHit2D[] _lookaheadBuffer = new RaycastHit2D[8];
    private static readonly Collider2D[]   _fleeBuffer      = new Collider2D[16];

    private static readonly Vector2[] _scanDirs = new Vector2[]
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

    private void Reset()
    {
        _renderer  = GetComponent<SpriteRenderer>();
        _explode   = GetComponent<Explode>();
        _particles = GetComponentInChildren<ParticleSystem>();
    }

    private void Awake()
    {
        _rb        = GetComponent<Rigidbody2D>();
        _spawnTime = Time.time;

        if (_renderer  == null) _renderer  = GetComponent<SpriteRenderer>();
        if (_explode   == null) _explode   = GetComponent<Explode>();
        if (_particles == null) _particles = GetComponentInChildren<ParticleSystem>();

        if (_rb != null)
        {
            _rb.gravityScale   = 0f;
            _rb.linearDamping  = 0f;
            _rb.angularDamping = 0f;
            _rb.constraints    = RigidbodyConstraints2D.FreezeRotation;
        }
    }

    public void Setup(InstrumentTrack track, float difficulty01, DrumTrack drum)
    {
        AssignedTrack  = track;
        _drum          = drum;
        _spawnTime     = Time.time;
        _loopsLived    = 0;

        _speed         = Mathf.Lerp(minSpeed, maxSpeed, difficulty01);
        _lifetimeLoops = Mathf.RoundToInt(Mathf.Lerp(maxLifetimeLoops, minLifetimeLoops, difficulty01));

        if (track != null)
        {
            if (_renderer != null) _renderer.color = track.DisplayColor;
            _explode?.SetTint(track.DisplayColor);
        }

        if (drum != null)
        {
            drum.OnLoopBoundary += OnLoopBoundary;
            _dustGen = drum.GetComponentInChildren<CosmicDustGenerator>()
                       ?? Object.FindAnyObjectByType<CosmicDustGenerator>();
        }

        if (_particles != null)
        {
            var main = _particles.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
        }

        _carveDir          = Random.insideUnitCircle.normalized;
        _isInBurst         = true;
        _burstTimer        = burstDuration;
        _nextScanAt        = Time.time;
        _pathCommitUntil   = 0f;
        _lastStallCheckPos = transform.position;
        _nextStallCheckAt  = Time.time + stallCheckInterval;
        _stallHits         = 0;

        RunDirectionScan();
    }

    private void OnDisable()
    {
        if (_drum != null) _drum.OnLoopBoundary -= OnLoopBoundary;
    }

    private void OnDestroy()
    {
        if (_drum != null) _drum.OnLoopBoundary -= OnLoopBoundary;
    }

    private void FixedUpdate()
    {
        if (_collected || _rb == null) return;

        Vector2 myPos = _rb.position;

        // Flee overrides everything
        var fleeVehicle = GetNearestVehicleWithin(myPos, fleeRadius);
        if (fleeVehicle != null)
        {
            Vector2 away = myPos - (Vector2)fleeVehicle.transform.position;
            if (away.sqrMagnitude < 0.001f) away = Random.insideUnitCircle;
            ApplyVelocityBlend(away.normalized * (_speed * fleeSpeedMultiplier));
            ApplyVisualRotation();
            ClampToBounds();
            return;
        }

        UpdateBurstPause();

        // Rescan when commitment window expires and reaction delay has elapsed,
        // or immediately when non-matching dust is directly ahead
        bool commitExpired = Time.time >= _pathCommitUntil;
        bool scanReady     = Time.time >= _nextScanAt;
        bool wallAhead     = HasNonMatchingDustAhead(myPos, _carveDir);

        if ((commitExpired && scanReady) || wallAhead)
            RunDirectionScan();

        float speedMult = _isInBurst ? burstSpeedMultiplier : pauseSpeedMultiplier;
        ApplyVelocityBlend(_carveDir * (_speed * speedMult));

        ApplyVisualRotation();
        RunStallCheck(myPos);
        ClampToBounds();
    }

    private void UpdateBurstPause()
    {
        _burstTimer -= Time.fixedDeltaTime;
        if (_burstTimer > 0f) return;

        _isInBurst  = !_isInBurst;
        _burstTimer = _isInBurst ? burstDuration : pauseDuration;

        if (_isInBurst)
        {
            _explode?.SetBurstDirection(_carveDir);
            _explode?.PreExplode();
            RunDirectionScan();
        }
    }

    private void RunDirectionScan()
    {
        Vector2 myPos     = _rb != null ? _rb.position : (Vector2)transform.position;
        float   bestScore = float.MinValue;
        Vector2 bestDir   = _carveDir;

        foreach (var dir in _scanDirs)
        {
            float score = ScoreDirection(myPos, dir);
            if (score > bestScore) { bestScore = score; bestDir = dir; }
        }

        float jitterRad = Random.Range(-turnJitterDeg, turnJitterDeg) * Mathf.Deg2Rad;
        float cos = Mathf.Cos(jitterRad), sin = Mathf.Sin(jitterRad);
        _carveDir = new Vector2(
            bestDir.x * cos - bestDir.y * sin,
            bestDir.x * sin + bestDir.y * cos).normalized;

        _nextScanAt      = Time.time + Random.Range(reactionDelayMin, reactionDelayMax);
        _pathCommitUntil = Time.time + pathCommitDuration;
    }

    private float ScoreDirection(Vector2 pos, Vector2 dir)
    {
        float score = 0f;

        float dot = Vector2.Dot(dir, _carveDir);
        if (dot < -0.5f) return 0.1f;  // U-turn rejection
        if (dot > 0f) score += continuityBias * dot;

        // Forward lookahead
        Vector2 rayStart = pos + dir * 0.25f;
        int count = Physics2D.Raycast(rayStart, dir, ContactFilter2D.noFilter, _lookaheadBuffer, lookaheadDistance * 2f);
        for (int i = 0; i < count; i++)
        {
            var hit = _lookaheadBuffer[i];
            if (!hit.collider) continue;
            if (hit.collider.attachedRigidbody == _rb) continue;
            var dust = hit.collider.GetComponent<CosmicDust>();
            if (dust == null) continue;

            if (AssignedTrack != null && dust.Role == AssignedTrack.assignedRole)
                score += 3.5f;   // strong pull toward matching territory
            else
                score -= 1.0f;   // mild penalty; physics bounce handles the deflection
            break;
        }

        // Side wall check — reward staying near any dust (wall-hugging)
        Vector2 perp = new Vector2(-dir.y, dir.x);
        if (QuickDustCheck(pos, perp, sideCheckDistance) || QuickDustCheck(pos, -perp, sideCheckDistance))
            score += wallHugBonus;

        return score;
    }

    private bool QuickDustCheck(Vector2 pos, Vector2 dir, float distance)
    {
        int count = Physics2D.Raycast(pos + dir * 0.1f, dir, ContactFilter2D.noFilter, _lookaheadBuffer, distance);
        for (int i = 0; i < count; i++)
        {
            var hit = _lookaheadBuffer[i];
            if (!hit.collider) continue;
            if (hit.collider.attachedRigidbody == _rb) continue;
            if (hit.collider.GetComponent<CosmicDust>() != null) return true;
        }
        return false;
    }

    private void RunStallCheck(Vector2 myPos)
    {
        if (Time.time < _nextStallCheckAt) return;
        _nextStallCheckAt = Time.time + stallCheckInterval;

        float moved = Vector2.Distance(myPos, _lastStallCheckPos);
        _lastStallCheckPos = myPos;

        if (moved < stallMoveThreshold)
        {
            _stallHits++;
            if (_stallHits >= 2)
            {
                float escapeRad = Random.Range(140f, 200f) * Mathf.Deg2Rad;
                float cos = Mathf.Cos(escapeRad), sin = Mathf.Sin(escapeRad);
                _carveDir = new Vector2(
                    _carveDir.x * cos - _carveDir.y * sin,
                    _carveDir.x * sin + _carveDir.y * cos).normalized;

                _stallHits       = 0;
                _isInBurst       = true;
                _burstTimer      = burstDuration;
                _pathCommitUntil = 0f;
                RunDirectionScan();
            }
        }
        else
        {
            _stallHits = 0;
        }
    }

    private void ApplyVelocityBlend(Vector2 desiredVel)
    {
        float blend = Mathf.Clamp01(turnRate * (1f - hesitation) * Time.fixedDeltaTime);
        _rb.linearVelocity = Vector2.Lerp(_rb.linearVelocity, desiredVel, blend);
    }

    private void ApplyVisualRotation()
    {
        float spd = _rb.linearVelocity.magnitude;
        if (spd < 0.05f) return;

        float targetAngle = Mathf.Atan2(_rb.linearVelocity.y, _rb.linearVelocity.x) * Mathf.Rad2Deg - 90f;
        _facedAngle = Mathf.MoveTowardsAngle(_facedAngle, targetAngle, faceTurnRate * Time.fixedDeltaTime);

        float wobbleHz = Mathf.Lerp(wobbleHzRange.x, wobbleHzRange.y, Mathf.Clamp01(spd / maxSpeed));
        _wobblePhase += Time.fixedDeltaTime * wobbleHz * Mathf.PI * 2f;

        transform.rotation = Quaternion.Euler(0f, 0f, _facedAngle + Mathf.Sin(_wobblePhase) * wobbleDeg);
    }

    private void ClampToBounds()
    {
        if (_drum != null && _drum.TryGetPlayAreaWorld(out var area))
            SuperNodeSteeringMath.ClampToBounds(_rb,
                Rect.MinMaxRect(area.left, area.bottom, area.right, area.top));
    }

    private bool HasNonMatchingDustAhead(Vector2 pos, Vector2 dir)
    {
        Vector2 rayStart = pos + dir * 0.25f;
        int count = Physics2D.Raycast(rayStart, dir, ContactFilter2D.noFilter, _lookaheadBuffer, lookaheadDistance);
        for (int i = 0; i < count; i++)
        {
            var hit = _lookaheadBuffer[i];
            if (!hit.collider) continue;
            if (hit.collider.attachedRigidbody == _rb) continue;
            var dust = hit.collider.GetComponent<CosmicDust>();
            if (!dust) continue;
            if (AssignedTrack != null && dust.Role == AssignedTrack.assignedRole) continue;
            return true;
        }
        return false;
    }

    private Vehicle GetNearestVehicleWithin(Vector2 pos, float radius)
    {
        int count = Physics2D.OverlapCircle(pos, radius, ContactFilter2D.noFilter, _fleeBuffer);
        Vehicle nearest  = null;
        float   nearestSq = radius * radius;
        for (int i = 0; i < count; i++)
        {
            var v = _fleeBuffer[i]?.GetComponentInParent<Vehicle>();
            if (v == null) continue;
            float dsq = ((Vector2)v.transform.position - pos).sqrMagnitude;
            if (dsq < nearestSq) { nearestSq = dsq; nearest = v; }
        }
        return nearest;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (_collected || Time.time - _spawnTime < spawnGraceSeconds) return;
        if (other.GetComponentInParent<Vehicle>() != null)
            Collect();
    }

    private void OnCollisionEnter2D(Collision2D col)
    {
        if (_dustGen == null || _drum == null || col.collider == null) return;
        var dust = col.collider.GetComponent<CosmicDust>();
        if (dust == null) return;
        if (AssignedTrack != null && dust.Role != AssignedTrack.assignedRole) return;
        Vector2Int cell = _drum.WorldToGridPosition(dust.transform.position);
        _dustGen.CarveCellPreserveGray(cell, dustFadeSeconds, dustRegrowDelay);
    }

    private void Collect()
    {
        _collected = true;

        if (AssignedTrack != null)
        {
            var rings = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
            rings?.SetSuperNodeMode(true);
            int pre = AssignedTrack.loopMultiplier;
            AssignedTrack.InstantFillAllBins(toMaxCapacity: true);
            int post = AssignedTrack.loopMultiplier;

            if (post > pre)
            {
                int binSz = AssignedTrack.BinSize();
                AssignedTrack.controller?.StartSuperNodeCompletionSequence(
                    AssignedTrack, pre, post, ascendLoopsOverride, binSz, _drum);
            }
            else
            {
                AssignedTrack.controller?.CheckAndTriggerAllTracksMaxed();
            }

            AssignedTrack.controller?.StageAltChordIfAllTracksMaxed();
        }

        Resolve(wasCaught: true);
    }

    private void Expire()
    {
        _collected = true;
        Resolve(wasCaught: false);
    }

    private void Resolve(bool wasCaught)
    {
        if (_resolved) return;
        _resolved = true;
        OnResolved?.Invoke(wasCaught);
        _explode?.Permanent();
        if (_explode == null) Destroy(gameObject);
    }

    private void OnLoopBoundary()
    {
        if (_collected) return;
        _loopsLived++;
        if (_loopsLived >= _lifetimeLoops)
            Expire();
    }
}
