using UnityEngine;

/// <summary>
/// The black-hole mass of one path exit: a near-black disc with a slowly turning accretion rim,
/// centered on the exit's reachable boundary line. Built by PathExitParticleBuilder; driven by
/// MazePathExit (Swallow when a vehicle is captured, Collapse when the exit loses).
/// The dust drawn into it is separate ParticleSystems under the same root.
/// </summary>
public class PathExitPortal : MonoBehaviour
{
    private enum State { Opening, Idle, Swallowing, Collapsing }

    private static readonly Color MassColor = new Color(0.01f, 0.01f, 0.03f, 1f);

    private SpriteRenderer _mass;
    private SpriteRenderer _rim;
    private SpriteRenderer _rim2;
    private ParticleSystem[] _systems;
    private Color _rimColor;
    private Color _rim2Color;
    private float _alpha = 1f;
    private float _rimSpin;
    private float _baseRadius;
    private float _radius;

    private State _state = State.Opening;
    private float _stateStart;
    private float _stateDuration = 0.5f;
    private float _from;
    private float _to;

    public float BaseRadius => _baseRadius;
    public float Radius => _radius;

    public void Init(
        SpriteRenderer mass, SpriteRenderer rim, SpriteRenderer rim2,
        Color rimColor, Color rim2Color, float baseRadius, float rimAlpha, float rimSpinDegPerSec)
    {
        _mass = mass;
        _rim = rim;
        _rim2 = rim2;
        _rimColor = rimColor;
        _rim2Color = rim2Color;
        _alpha = Mathf.Clamp01(rimAlpha);
        _rimSpin = rimSpinDegPerSec;
        _baseRadius = baseRadius;
        _systems = GetComponentsInChildren<ParticleSystem>(true);
        _radius = 0f;
        Enter(State.Opening, 0.5f, 0f, baseRadius);
        Apply();
    }

    /// <summary>A vehicle was captured: swell well past the boundary — through the UI band and
    /// beyond the true screen edge (the mass is centered on it) — while the dust is pulled in faster.</summary>
    public void Swallow()
    {
        Enter(State.Swallowing, 0.7f, _radius, _baseRadius * 2.2f);
        SetStreamSpeed(2.5f);
    }

    /// <summary>This exit lost: close like a dying hole.</summary>
    public void Collapse(float seconds)
    {
        if (_state == State.Collapsing || _state == State.Swallowing) return;
        Enter(State.Collapsing, Mathf.Max(0.05f, seconds), _radius, 0f);
    }

    /// <summary>Close from ANY state (including mid-swallow): shrink and fade from the current size.</summary>
    public void Close(float seconds)
    {
        if (_state == State.Collapsing) return;
        Enter(State.Collapsing, Mathf.Max(0.05f, seconds), _radius, 0f);
        StopEmitting();
    }

    /// <summary>Close, stop emitting, and destroy the whole visual once everything has faded.
    /// For portals that have no MazePathExit owning their lifetime (the entry hole).</summary>
    public void CloseAndDestroy(float seconds)
    {
        Close(seconds);
        // Longest dust lifetime is ~2s; let in-flight motes finish falling into the shrinking mass.
        Destroy(gameObject, Mathf.Max(seconds, 0.1f) + 1.5f);
    }

    private void StopEmitting()
    {
        if (_systems == null) return;
        foreach (var ps in _systems)
            if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    private void Enter(State s, float duration, float from, float to)
    {
        _state = s;
        _stateStart = Time.time;
        _stateDuration = duration;
        _from = from;
        _to = to;
    }

    private void SetStreamSpeed(float speed)
    {
        if (_systems == null) return;
        foreach (var ps in _systems)
        {
            if (ps == null) continue;
            var main = ps.main;
            main.simulationSpeed = speed;
        }
    }

    private void Update()
    {
        float t = Mathf.Clamp01((Time.time - _stateStart) / Mathf.Max(0.0001f, _stateDuration));
        switch (_state)
        {
            case State.Opening:
                _radius = Mathf.Lerp(_from, _to, EaseOutBack(t));
                if (t >= 1f) _state = State.Idle;
                break;
            case State.Swallowing:
                _radius = Mathf.Lerp(_from, _to, 1f - (1f - t) * (1f - t));
                break;
            case State.Collapsing:
                _radius = Mathf.Lerp(_from, _to, t * t * (3f - 2f * t));
                break;
            default:
                // Idle: gentle breathing so the mass reads as alive, not a static sprite.
                _radius = _baseRadius * (1f + 0.04f * Mathf.Sin(Time.time * 2.2f));
                break;
        }
        Apply();
    }

    private void Apply()
    {
        float fade = _state == State.Collapsing
            ? 1f - Mathf.Clamp01((Time.time - _stateStart) / Mathf.Max(0.0001f, _stateDuration))
            : 1f;
        float diameter = Mathf.Max(0.0001f, _radius * 2f);

        if (_mass != null)
        {
            _mass.transform.localScale = Vector3.one * diameter;
            var c = MassColor; c.a = fade;
            _mass.color = c;
        }
        if (_rim != null)
        {
            _rim.transform.localScale = Vector3.one * diameter * 1.25f;
            _rim.transform.localRotation = Quaternion.Euler(0f, 0f, Time.time * _rimSpin);
            var c = _rimColor; c.a = _alpha * fade;
            _rim.color = c;
        }
        if (_rim2 != null)
        {
            _rim2.transform.localScale = Vector3.one * diameter * 1.5f;
            _rim2.transform.localRotation = Quaternion.Euler(0f, 0f, -Time.time * _rimSpin * 0.6f);
            var c = _rim2Color; c.a = _alpha * 0.7f * fade;
            _rim2.color = c;
        }
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }
}
