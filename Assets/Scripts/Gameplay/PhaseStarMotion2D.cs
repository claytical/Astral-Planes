using UnityEngine;
using System;
using Random = UnityEngine.Random;

[RequireComponent(typeof(Rigidbody2D))]
[DisallowMultipleComponent]
public sealed class PhaseStarMotion2D : MonoBehaviour
{
    public event Action<Vector2> OnVelocityChanged;
    public Func<Vector2, float, Vector2> ExternalPathStep; 
    Rigidbody2D _rb;
    PhaseStarBehaviorProfile _profile;
    bool _enabled;
    Vector2 _driftDir;
    float _rechooseTimer;
    [SerializeField] bool kinematicMode = false;     // toggle in Inspector or at runtime
    [SerializeField] float edgeBounce = 0.9f;        // reflect drift a bit at edges
    [SerializeField] float screenPadding = 0.5f;     // keep it slightly inside the frame
    [SerializeField] float focusSpeedMul = 0.5f;     // slows during focus mode

    bool _focus;  // set from PhaseStar
    Camera _cam;


    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = 0f;
        _rb.constraints  = RigidbodyConstraints2D.FreezeRotation;
        _cam = Camera.main;
        _driftDir = Random.insideUnitCircle.normalized;
        _rechooseTimer = 0f;

        ApplyBodyType();
    }
// ADD:
    public void SetKinematicMode(bool on)
    {
        kinematicMode = on;
        ApplyBodyType();
    }

    public void SetFocusMode(bool on) => _focus = on;
    void KeepInsideScreenAndBounce()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        var pos = _rb.position;
        float z = 0f;

        var min = (Vector2)_cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)) + Vector2.one * screenPadding;
        var max = (Vector2)_cam.ViewportToWorldPoint(new Vector3(1f, 1f, z)) - Vector2.one * screenPadding;

        bool hit = false;
        if (pos.x < min.x) { pos.x = min.x; _driftDir.x = Mathf.Abs(_driftDir.x) * edgeBounce; hit = true; }
        else if (pos.x > max.x) { pos.x = max.x; _driftDir.x = -Mathf.Abs(_driftDir.x) * edgeBounce; hit = true; }

        if (pos.y < min.y) { pos.y = min.y; _driftDir.y = Mathf.Abs(_driftDir.y) * edgeBounce; hit = true; }
        else if (pos.y > max.y) { pos.y = max.y; _driftDir.y = -Mathf.Abs(_driftDir.y) * edgeBounce; hit = true; }

        if (hit)
        {
            _driftDir.Normalize();
            if (kinematicMode) _rb.position = pos;
            else _rb.MovePosition(pos); // cheap corrector for dynamic too
        }
    }
    void ApplyBodyType()
    {
        if (!_rb) return;
        _rb.bodyType = kinematicMode ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
        _rb.linearVelocity = Vector2.zero;
        _rb.angularVelocity = 0f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
    }

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;

        // Drive enable/disable from PhaseStar events
        star.OnArmed += s => { Enable(true);  };
        star.OnDisarmed += s => { Enable(false); };

    }

    public void Enable(bool on)
    {
        _enabled = on;
        if (!_rb) return;

        if (!on)
        {
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.constraints = RigidbodyConstraints2D.FreezeAll;
        }
        else
        {
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        }
    }

    void FixedUpdate()
    {
        if (!_enabled || _profile == null || !_rb) {
            if (!_enabled) Debug.Log("[Motion2D] disabled");
            if (_profile == null) Debug.Log("[Motion2D] no profile");
            return;
        }
        float speed  = Mathf.Max(0f, _profile.starDriftSpeed);
        float jitter = Mathf.Max(0f, _profile.starDriftJitter);
        if (_focus) speed *= Mathf.Clamp(focusSpeedMul, 0.1f, 1f); // slow in focus

        _rechooseTimer -= Time.fixedDeltaTime;
        if (_rechooseTimer <= 0f) PickNewDriftDir();

        Vector2 j = Random.insideUnitCircle * jitter;
        Vector2 v = (_driftDir + j).normalized * speed;
        if (kinematicMode && ExternalPathStep != null)
        {
            var next = ExternalPathStep(_rb.position, Time.fixedDeltaTime);
            _rb.MovePosition(next);
        }
        if (kinematicMode)
            _rb.MovePosition(_rb.position + v * Time.fixedDeltaTime);
        else
            _rb.linearVelocity = v;

        KeepInsideScreenAndBounce();

        OnVelocityChanged?.Invoke(kinematicMode ? v : _rb.linearVelocity);

        // Teleport spice (Wildcard)
        if (_profile.teleportChancePerSec > 0f &&
            UnityEngine.Random.value < _profile.teleportChancePerSec * Time.fixedDeltaTime)
        {
            Vector2 off = UnityEngine.Random.insideUnitCircle * 1.5f;
            _rb.position += off;
            OnVelocityChanged?.Invoke(kinematicMode ? v : _rb.linearVelocity);
        }
    }

    void PickNewDriftDir()
    {
        Vector2 rnd = Random.insideUnitCircle.normalized;
        float bias  = Mathf.Clamp01(_profile.orbitBias);
        _driftDir   = Vector2.Lerp(rnd, Vector2.Perpendicular(rnd).normalized, bias).normalized;
        _rechooseTimer = UnityEngine.Random.Range(1.2f, 2.4f);
    }
}
