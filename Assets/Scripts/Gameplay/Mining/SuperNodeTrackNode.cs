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

    [Header("Movement")]
    [SerializeField] private float acceleration      = 5f;
    [SerializeField] private float turnRateDegPerSec = 90f;
    [SerializeField] private float waypointArrivalRadius   = 1.5f;
    [SerializeField] private float waypointChangeInterval  = 3f;
    [SerializeField] private float waypointMargin          = 2f;

    [Header("Collection")]
    [SerializeField] private float spawnGraceSeconds = 0.2f;
    [SerializeField] private float minImpactSpeed    = 1.5f;
    [SerializeField] private int   ascendLoopsOverride = 3;

    [Header("Dust Carving")]
    [SerializeField] private float dustFadeSeconds = 0.15f;
    [SerializeField] private float dustRegrowDelay = 2f;

    public InstrumentTrack AssignedTrack  { get; private set; }
    public System.Action<bool> OnResolved;

    private float  _spawnTime;
    private bool   _collected;
    private bool   _resolved;
    private int    _loopsLived;
    private int    _lifetimeLoops;
    private float  _speed;

    private Rigidbody2D         _rb;
    private DrumTrack           _drum;
    private CosmicDustGenerator _dustGen;

    private Vector2 _targetWaypoint;
    private float   _waypointTimer;
    private bool    _hasWaypoint;

    private void Reset()
    {
        _renderer = GetComponent<SpriteRenderer>();
        _explode  = GetComponent<Explode>();
    }

    private void Awake()
    {
        _rb        = GetComponent<Rigidbody2D>();
        _spawnTime = Time.time;

        if (_renderer == null) _renderer = GetComponent<SpriteRenderer>();
        if (_explode   == null) _explode  = GetComponent<Explode>();

        if (_rb != null)
        {
            _rb.gravityScale   = 0f;
            _rb.linearDamping  = 0f;
            _rb.angularDamping = 0f;
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
            if (_renderer != null) _renderer.color = track.trackColor;
            _explode?.SetTint(track.trackColor);
        }

        if (drum != null)
        {
            drum.OnLoopBoundary += OnLoopBoundary;
            _dustGen = drum.GetComponentInChildren<CosmicDustGenerator>()
                       ?? Object.FindAnyObjectByType<CosmicDustGenerator>();
        }

        PickNewWaypoint();
    }

    private void OnDisable()
    {
        if (_drum != null)
            _drum.OnLoopBoundary -= OnLoopBoundary;
    }

    private void OnDestroy()
    {
        if (_drum != null)
            _drum.OnLoopBoundary -= OnLoopBoundary;
    }

    private void FixedUpdate()
    {
        if (_collected || _rb == null) return;

        _waypointTimer -= Time.fixedDeltaTime;

        Vector2 myPos        = _rb.position;
        float   distToTarget = (_targetWaypoint - myPos).magnitude;

        if (!_hasWaypoint || _waypointTimer <= 0f || distToTarget < waypointArrivalRadius)
            PickNewWaypoint();

        Vector2 toTarget   = _targetWaypoint - myPos;
        Vector2 desiredDir = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : Vector2.up;
        Vector2 desiredVel = desiredDir * _speed;

        _rb.linearVelocity = SuperNodeSteeringMath.SteerTowards(
            _rb.linearVelocity, desiredVel, turnRateDegPerSec, acceleration, Time.fixedDeltaTime);

        if (_drum != null && _drum.TryGetPlayAreaWorld(out var area))
        {
            var bounds = Rect.MinMaxRect(area.left, area.bottom, area.right, area.top);
            SuperNodeSteeringMath.ClampToBounds(_rb, bounds);
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (_collected) return;
        if (Time.time - _spawnTime < spawnGraceSeconds) return;

        var vehicle = other.GetComponentInParent<Vehicle>();
        if (vehicle == null) return;

        var vrb = vehicle.GetComponent<Rigidbody2D>();
        if (vrb == null || vrb.linearVelocity.magnitude < minImpactSpeed) return;

        Collect();
    }

    private void OnCollisionEnter2D(Collision2D col)
    {
        if (_dustGen == null || col.collider == null) return;
        var dust = col.collider.GetComponent<CosmicDust>();
        if (dust == null) return;

        Vector2Int cell = _drum != null
            ? _drum.WorldToGridPosition(dust.transform.position)
            : default;
        if (_drum == null) return;

        _dustGen.CarveCellPreserveGray(cell, dustFadeSeconds, dustRegrowDelay);
    }

    private void Collect()
    {
        _collected = true;

        if (AssignedTrack != null)
        {
            var rings = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
            rings?.SetSuperNodeMode(true);  // keep record visible through all deformations
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

            // Stage alt chord before Resolve() triggers StarPool → BeginMotifBridge
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

    private void PickNewWaypoint()
    {
        _waypointTimer = waypointChangeInterval;
        _hasWaypoint   = false;

        if (_drum == null || !_drum.TryGetPlayAreaWorld(out var area)) return;

        var bounds = Rect.MinMaxRect(area.left, area.bottom, area.right, area.top);
        float x = Random.Range(bounds.xMin + waypointMargin, bounds.xMax - waypointMargin);
        float y = Random.Range(bounds.yMin + waypointMargin, bounds.yMax - waypointMargin);
        _targetWaypoint = new Vector2(x, y);
        _hasWaypoint    = true;
    }
}
