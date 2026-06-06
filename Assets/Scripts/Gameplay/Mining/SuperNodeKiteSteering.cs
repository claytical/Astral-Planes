using UnityEngine;

/// <summary>
/// Patrol-based movement for SuperNode. Selects random waypoints inside the play area
/// and glides toward them, letting physics handle dust collisions naturally.
/// </summary>
[DisallowMultipleComponent]
public class SuperNodeKiteSteering : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Rigidbody2D rb;
    [SerializeField] private SuperNode   superNode;
    [SerializeField] private DrumTrack   drumTrack;

    [Header("Speed")]
    [SerializeField] private float speed             = 2.5f;
    [SerializeField] private float acceleration      = 6f;
    [SerializeField] private float turnRateDegPerSec = 80f;

    [Header("Patrol")]
    [SerializeField] private float waypointArrivalRadius = 1.5f;
    [SerializeField] private float waypointChangeInterval = 4f;
    [SerializeField] private float waypointMargin = 2f;

    [Header("Bounds")]
    [SerializeField] private float edgeSoftnessWorld = 1.25f;

    private Vector2 _targetWaypoint;
    private float   _waypointTimer;
    private bool    _hasWaypoint;
    private GameFlowManager _gfm;

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
        PickNewWaypoint();
    }

    private void OnEnable()
    {
        _waypointTimer = 0f;
        ResolveDrumTrack();
    }

    private void FixedUpdate()
    {
        if (rb == null) return;

        _waypointTimer -= Time.fixedDeltaTime;

        Vector2 myPos        = rb.position;
        float   distToTarget = (_targetWaypoint - myPos).magnitude;

        if (!_hasWaypoint || _waypointTimer <= 0f || distToTarget < waypointArrivalRadius)
            PickNewWaypoint();

        Vector2 toTarget   = _targetWaypoint - myPos;
        Vector2 desiredDir = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : Vector2.up;
        Vector2 desiredVel = desiredDir * speed;

        rb.linearVelocity = SuperNodeSteeringMath.SteerTowards(
            rb.linearVelocity, desiredVel, turnRateDegPerSec, acceleration, Time.fixedDeltaTime);

        if (TryGetWorldBounds(out Rect bounds))
            SuperNodeSteeringMath.ClampToBounds(rb, bounds);
    }

    private void PickNewWaypoint()
    {
        _waypointTimer = waypointChangeInterval;
        _hasWaypoint   = false;

        if (!TryGetWorldBounds(out Rect bounds)) return;

        float x = Random.Range(bounds.xMin + waypointMargin, bounds.xMax - waypointMargin);
        float y = Random.Range(bounds.yMin + waypointMargin, bounds.yMax - waypointMargin);
        _targetWaypoint = new Vector2(x, y);
        _hasWaypoint    = true;
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
        if (superNode != null && superNode.drumTrack != null) { drumTrack = superNode.drumTrack; return; }
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm != null && _gfm.activeDrumTrack != null) { drumTrack = _gfm.activeDrumTrack; return; }
        drumTrack = FindAnyObjectByType<DrumTrack>();
    }
}
