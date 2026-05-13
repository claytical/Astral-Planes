using UnityEngine;

/// <summary>
/// Dust-aware organic evasion for SuperNode. Scores 8 candidate directions each update tick
/// against flee alignment and corridor clearness; cornering the node in dust slows it naturally.
/// </summary>
[DisallowMultipleComponent]
public class SuperNodeKiteSteering : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Rigidbody2D rb;
    [SerializeField] private SuperNode   superNode;
    [SerializeField] private DrumTrack   drumTrack;

    [Header("Speed")]
    [SerializeField] private float baseSpeed         = 2.5f;
    [SerializeField] private float maxSpeed          = 5.5f;
    [SerializeField] private float acceleration      = 12f;
    [SerializeField] private float turnRateDegPerSec = 130f;

    [Header("Flee")]
    [SerializeField] private float fleeRadius = 7f;
    [SerializeField] private float fleeWeight = 2.5f;

    [Header("Dust Awareness")]
    [SerializeField] private int   dustScanRadius             = 3;
    [SerializeField] private float corridorWeight             = 1.5f;
    [SerializeField] private float cornerednessSlowdownFactor = 0.9f;

    [Header("Timing")]
    [SerializeField] private float dirUpdateInterval      = 0.12f;
    [SerializeField] private float vehicleRefreshInterval = 0.25f;

    [Header("Bounds")]
    [SerializeField] private float edgeSoftnessWorld = 1.25f;

    private static readonly Vector2[] kCandidates = BuildCandidates();

    private Vector2  _desiredDir;
    private float    _targetSpeed;
    private float    _dirTimer;
    private float    _vehicleRefreshTimer;
    private Vehicle[] _vehicles;
    private GameFlowManager _gfm;

    private static Vector2[] BuildCandidates()
    {
        var dirs = new Vector2[8];
        for (int i = 0; i < 8; i++)
        {
            float rad = i * 45f * Mathf.Deg2Rad;
            dirs[i] = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        }
        return dirs;
    }

    private void Reset()
    {
        rb        = GetComponent<Rigidbody2D>();
        superNode = GetComponent<SuperNode>();
    }

    private void Awake()
    {
        if (rb        == null) rb        = GetComponent<Rigidbody2D>();
        if (superNode == null) superNode = GetComponent<SuperNode>();

        if (rb != null)
        {
            rb.gravityScale   = 0f;
            rb.linearDamping  = 0f;
            rb.angularDamping = 0f;
        }

        ResolveDrumTrack();
        RefreshVehicles();

        _desiredDir  = Random.insideUnitCircle.normalized;
        _targetSpeed = baseSpeed;
    }

    private void OnEnable()
    {
        _dirTimer            = 0f;
        _vehicleRefreshTimer = 0f;
        ResolveDrumTrack();
    }

    private void FixedUpdate()
    {
        if (rb == null) return;

        _dirTimer            -= Time.fixedDeltaTime;
        _vehicleRefreshTimer -= Time.fixedDeltaTime;

        if (_vehicleRefreshTimer <= 0f)
        {
            RefreshVehicles();
            _vehicleRefreshTimer = vehicleRefreshInterval;
        }

        if (_dirTimer <= 0f)
        {
            RecalculateDirection();
            _dirTimer = dirUpdateInterval;
        }

        Vector2 desiredVel = _desiredDir * _targetSpeed;
        rb.linearVelocity  = SuperNodeSteeringMath.SteerTowards(
            rb.linearVelocity, desiredVel, turnRateDegPerSec, acceleration, Time.fixedDeltaTime);

        if (TryGetWorldBounds(out Rect bounds))
            SuperNodeSteeringMath.ClampToBounds(rb, bounds);
    }

    public void BoltNow(Vector2 vehicleWorldPos)
    {
        Vector2 away = (rb.position - vehicleWorldPos);
        if (away.sqrMagnitude < 0.0001f) away = Vector2.up;
        rb.AddForce(away.normalized * (maxSpeed * rb.mass), ForceMode2D.Impulse);
        _dirTimer = 0f;
    }

    // ---------------------------------------------------------------
    private void RecalculateDirection()
    {
        if (drumTrack == null) { ResolveDrumTrack(); return; }

        Vector2     myPos  = rb.position;
        Vector2Int  myCell = drumTrack.CellOf(myPos);
        Vector2     flee   = ComputeFleeVector(myPos);

        bool   hasBounds  = TryGetWorldBounds(out Rect bounds);
        float  bestScore  = float.NegativeInfinity;
        Vector2 bestDir   = _desiredDir;
        int    blocked    = 0;

        Vector2 fleeNorm = flee.sqrMagnitude > 0.0001f ? flee.normalized : Vector2.zero;
        float   fleeIntensity = Mathf.Clamp01(flee.magnitude);

        for (int i = 0; i < kCandidates.Length; i++)
        {
            Vector2 candidate = kCandidates[i];

            float clear    = CountClearCells(myCell, candidate, dustScanRadius) / (float)dustScanRadius;
            float fleeDot  = Mathf.Clamp01(Vector2.Dot(candidate, fleeNorm)) * fleeIntensity;
            float edgePen  = hasBounds
                ? SuperNodeSteeringMath.EdgeFactor01(myPos + candidate * edgeSoftnessWorld, bounds, edgeSoftnessWorld)
                : 0f;

            float score = fleeDot * fleeWeight + clear * corridorWeight - edgePen;

            // Tie-break: add a small bias toward current direction to suppress jitter
            score += Vector2.Dot(candidate, _desiredDir) * 0.05f;

            if (clear < 0.4f) blocked++;

            if (score > bestScore) { bestScore = score; bestDir = candidate; }
        }

        float corneredness = blocked / (float)kCandidates.Length;
        _desiredDir  = bestDir;
        _targetSpeed = Mathf.Lerp(baseSpeed, maxSpeed, fleeIntensity)
                       * (1f - corneredness * cornerednessSlowdownFactor);
        _targetSpeed = Mathf.Max(_targetSpeed, 0.1f);
    }

    private Vector2 ComputeFleeVector(Vector2 myPos)
    {
        if (_vehicles == null) return Vector2.zero;

        float   best     = float.PositiveInfinity;
        Vector2 bestPos  = myPos;

        foreach (var v in _vehicles)
        {
            if (v == null) continue;
            float d = ((Vector2)v.transform.position - myPos).sqrMagnitude;
            if (d < best) { best = d; bestPos = v.transform.position; }
        }

        float dist = Mathf.Sqrt(best);
        if (dist >= fleeRadius) return Vector2.zero;

        Vector2 dir       = (myPos - bestPos);
        if (dir.sqrMagnitude < 0.0001f) dir = Vector2.up;
        float intensity   = Mathf.InverseLerp(fleeRadius, 0f, dist);
        return dir.normalized * intensity;
    }

    private int CountClearCells(Vector2Int origin, Vector2 dir, int radius)
    {
        int   clear    = 0;
        int   stepX    = Mathf.RoundToInt(dir.x);
        int   stepY    = Mathf.RoundToInt(dir.y);
        if (stepX == 0 && stepY == 0) return radius;

        Vector2Int cell = origin;
        for (int i = 1; i <= radius; i++)
        {
            cell = new Vector2Int(origin.x + stepX * i, origin.y + stepY * i);
            if (!drumTrack.HasDustAt(cell)) clear++;
        }
        return clear;
    }

    private bool TryGetWorldBounds(out Rect bounds)
    {
        bounds = default;
        if (drumTrack == null) return false;
        if (!drumTrack.TryGetPlayAreaWorld(out var area)) return false;
        bounds = Rect.MinMaxRect(area.left, area.bottom, area.right, area.top);
        return bounds.width > 0.01f && bounds.height > 0.01f;
    }

    private void ResolveDrumTrack()
    {
        if (drumTrack != null) return;

        if (superNode != null && superNode.drumTrack != null)
        {
            drumTrack = superNode.drumTrack;
            return;
        }

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm != null && _gfm.activeDrumTrack != null)
        {
            drumTrack = _gfm.activeDrumTrack;
            return;
        }

        drumTrack = FindAnyObjectByType<DrumTrack>();
    }

    private void RefreshVehicles()
    {
        _vehicles = FindObjectsOfType<Vehicle>(includeInactive: false);
    }
}
