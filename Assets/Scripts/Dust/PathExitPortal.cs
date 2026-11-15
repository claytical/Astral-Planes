using UnityEngine;

/// <summary>
/// The rainbow pathway of one path exit: a road of touching color stripes running from inside the
/// carved lane out past the true screen edge, its texture flowing outward. Built by
/// PathExitParticleBuilder; driven by MazePathExit (Swallow when a vehicle is captured, Collapse
/// when the exit loses). The streamers and halo dust are separate ParticleSystems under the same root.
/// </summary>
public class PathExitPortal : MonoBehaviour
{
    private enum State { Opening, Idle, Swallowing, Collapsing }

    private Transform _road;
    private Material _roadMat;
    private Texture2D _roadTex;
    private Mesh _roadMesh;
    private ParticleSystem[] _systems;
    private float _roadAlpha = 1f;
    private float _length = 10f;
    private float _innerDistance;
    private float _baseWidth;
    private float _width;
    private float _reveal;               // 0..1 of the road's length unrolled
    private float _flowSpeed = 0.9f;
    private float _flowMul = 1f;
    private float _glow;                 // 0..1 extra brightness while swallowing
    private float _tilesPerUnit = 0.5f;
    private float _swallowSwell = 1.35f;
    private float _swallowSeconds = 0.6f;
    private float _scroll;
    private bool _reverseFlow;           // entry road: texture and ripples flow inward

    // Gravitational vibration: the road's side rows ripple outward along its length.
    private Vector3[] _baseVerts;
    private Vector3[] _verts;
    private int _rows;
    private int _cols;
    private float _vibAmp;
    private float _vibFreq;
    private float _vibSpeed;
    private float _vibPhase;
    private float _vibMul = 1f;

    // Capture surge.
    private float _surgeBoost = 4f;
    private int _surgeBurst = 40;
    private bool _surged;

    private State _state = State.Opening;
    private float _stateStart;
    private float _stateDuration = 0.5f;
    private float _from;
    private float _to;

    public float BaseWidth => _baseWidth;
    public float Width => _width;
    /// <summary>Distance from the screen-edge anchor (root position) back to the road's inner end.</summary>
    public float InnerDistance => _innerDistance;

    /// <summary>Wires the built road to its config. <paramref name="roadMesh"/> must have
    /// PathExitParticleBuilder.RoadRows rows of vertices (outer rows = feathered edges).</summary>
    public void Init(
        PathExitConfig cfg, PathExitConfig.RoadLook look,
        Transform road, Material roadMat, Texture2D roadTex, Mesh roadMesh,
        float baseWidth, float length, float innerDistance, bool reverseFlow = false)
    {
        _reverseFlow = reverseFlow;
        _road = road;
        _roadMat = roadMat;
        _roadTex = roadTex;
        _roadMesh = roadMesh;
        _baseWidth = baseWidth;
        _length = Mathf.Max(0.01f, length);
        _innerDistance = innerDistance;

        _roadAlpha = Mathf.Clamp01(look.roadAlpha);
        _flowSpeed = look.flowSpeed;
        _tilesPerUnit = cfg.textureTilesPerUnit;
        _swallowSwell = cfg.swallowWidthScale;
        _swallowSeconds = cfg.swallowSeconds;
        _surgeBoost = Mathf.Max(1f, cfg.captureEmissionBoost);
        _surgeBurst = Mathf.Max(0, cfg.captureBurst);
        _vibAmp = cfg.vibrationAmplitude;
        _vibFreq = cfg.vibrationFrequency;
        _vibSpeed = cfg.vibrationSpeed;

        if (_roadMesh != null)
        {
            _baseVerts = _roadMesh.vertices;
            _verts = (Vector3[])_baseVerts.Clone();
            _rows = PathExitParticleBuilder.RoadRows;
            _cols = _baseVerts.Length / _rows;
        }

        _systems = GetComponentsInChildren<ParticleSystem>(true);
        _width = baseWidth;
        _reveal = 0f;
        Enter(State.Opening, Mathf.Max(0.01f, cfg.openSeconds), 0f, 1f);
        Apply();
    }

    private void OnDestroy()
    {
        if (_roadMat != null) Destroy(_roadMat);
        if (_roadTex != null) Destroy(_roadTex);
        if (_roadMesh != null) Destroy(_roadMesh);
    }

    /// <summary>A vehicle was captured: the road widens, brightens and flows faster while the
    /// streamers race outward.</summary>
    public void Swallow()
    {
        _reveal = 1f;
        Enter(State.Swallowing, _swallowSeconds, _width, _baseWidth * _swallowSwell);
        SetStreamSpeed(2.5f);
        Surge();
    }

    // Capture: every emitter bursts and keeps pouring at a multiple of its rate, so entering the
    // road reads unmistakably as having switched something on.
    private void Surge()
    {
        if (_surged || _systems == null) return;
        _surged = true;
        foreach (var ps in _systems)
        {
            if (ps == null) continue;
            var em = ps.emission;
            em.rateOverTimeMultiplier *= _surgeBoost;
            var main = ps.main;
            main.maxParticles = Mathf.CeilToInt(main.maxParticles * _surgeBoost) + _surgeBurst;
            if (_surgeBurst > 0) ps.Emit(_surgeBurst);
        }
    }

    /// <summary>This exit lost: the road narrows away and fades.</summary>
    public void Collapse(float seconds)
    {
        if (_state == State.Collapsing || _state == State.Swallowing) return;
        Enter(State.Collapsing, Mathf.Max(0.05f, seconds), _width, 0f);
    }

    /// <summary>Close from ANY state (including mid-swallow): narrow and fade from the current width.</summary>
    public void Close(float seconds)
    {
        if (_state == State.Collapsing) return;
        Enter(State.Collapsing, Mathf.Max(0.05f, seconds), _width, 0f);
        StopEmitting();
    }

    /// <summary>Close, stop emitting, and destroy the whole visual once everything has faded.
    /// For portals that have no MazePathExit owning their lifetime (the entry road).</summary>
    public void CloseAndDestroy(float seconds)
    {
        Close(seconds);
        // Let in-flight streamers/motes finish while the road narrows away.
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
                // The road unrolls from the lane out to the screen edge.
                _reveal = Mathf.Lerp(_from, _to, 1f - (1f - t) * (1f - t) * (1f - t));
                _width = _baseWidth;
                if (t >= 1f) _state = State.Idle;
                break;
            case State.Swallowing:
            {
                float e = 1f - (1f - t) * (1f - t);
                _width = Mathf.Lerp(_from, _to, e);
                _flowMul = Mathf.Lerp(1f, 3f, e);
                _vibMul = Mathf.Lerp(1f, 2.2f, e);
                _glow = e;
                break;
            }
            case State.Collapsing:
                _width = Mathf.Lerp(_from, _to, t * t * (3f - 2f * t));
                break;
            default:
                // Idle: gentle breathing so the road reads as alive, not a static strip.
                _width = _baseWidth * (1f + 0.03f * Mathf.Sin(Time.time * 2.2f));
                break;
        }
        Vibrate();
        Apply();
    }

    // Side rows ripple with a traveling wave (moving outward, toward the edge the road pulls to)
    // plus a fast, fine hum; the inner feather rows follow at reduced strength so the soft edge
    // band breathes rather than tearing. Amplitude is in fractions of the road's width.
    private void Vibrate()
    {
        if (_baseVerts == null || _rows < 2 || _vibAmp <= 0f) return;
        _vibPhase += Time.deltaTime * _vibSpeed * _flowMul * (_reverseFlow ? -1f : 1f);
        float amp = _vibAmp * _vibMul;
        float t = Time.time;
        for (int r = 0; r < _rows; r++)
        {
            bool top = r >= _rows / 2;
            bool outer = r == 0 || r == _rows - 1;
            float rowAmp = amp * (outer ? 1f : 0.45f);
            float sideSeed = top ? 37.1f : 11.3f;
            float sign = top ? 1f : -1f;
            for (int i = 0; i < _cols; i++)
            {
                int vi = r * _cols + i;
                var bv = _baseVerts[vi];
                float d = bv.x * _length;   // world distance along the road
                float wave = Mathf.Sin((d * _vibFreq - _vibPhase) * Mathf.PI * 2f + sideSeed);
                float hum = Mathf.PerlinNoise(d * 3.1f + sideSeed, t * 9f) * 2f - 1f;
                float off = rowAmp * (0.65f * wave + 0.35f * hum);
                _verts[vi] = new Vector3(bv.x, bv.y + sign * off, bv.z);
            }
        }
        _roadMesh.vertices = _verts;
    }

    private void Apply()
    {
        float fade = _state == State.Collapsing
            ? 1f - Mathf.Clamp01((Time.time - _stateStart) / Mathf.Max(0.0001f, _stateDuration))
            : 1f;

        float len = Mathf.Max(0.0001f, _length * _reveal);
        if (_road != null)
            _road.localScale = new Vector3(len, Mathf.Max(0.0001f, _width), 1f);

        if (_roadMat != null)
        {
            // Texture density stays fixed in world units while the road unrolls. Exit roads flow
            // outward; entry roads mirror the texture (chevrons point inward) and flow inward.
            float dir = _reverseFlow ? -1f : 1f;
            _roadMat.mainTextureScale = new Vector2(dir * len * _tilesPerUnit, 1f);
            _scroll += Time.deltaTime * _flowSpeed * _flowMul;
            _roadMat.mainTextureOffset = new Vector2(-dir * _scroll, 0f);

            // Brightness pulse only ever brightens (> 1 on the tint); alpha carries the fade.
            float pulse = 1f + 0.08f * Mathf.Sin(Time.time * 3.1f) + 0.35f * _glow;
            var c = new Color(pulse, pulse, pulse, Mathf.Clamp01(_roadAlpha + 0.2f * _glow) * fade);
            _roadMat.color = c;
        }
    }
}
