using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[DisallowMultipleComponent]
public class SuperNode : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private SoloVoice soloVoice;
    [SerializeField] public DrumTrack drumTrack;
    [SerializeField] private SuperNodeCosmicDustWaveDriver dustWaveDriver;

    [Header("Collision")]
    [SerializeField] private float spawnCollisionGraceSeconds = 0.15f;
    [SerializeField] private float minImpactSpeed = 2.0f;
    [SerializeField] private bool despawnOnNextBoundary = false;

    [Header("Safe local collision audio")]
    [SerializeField] private bool playSafeCollisionAudio = true;
    [SerializeField] private float collisionAudioVelocity = 0.85f;
    [SerializeField] private int collisionAudioTicks = 30;

    public Action OnResolved;

    private float _spawnTime;
    private bool _sawFirstBoundary;
    private bool _resolvedFired;
    private bool _commitPending;

    private readonly Dictionary<MusicalRole, InstrumentTrack> _trackByRole = new();
    private readonly HashSet<MusicalRole> _activeRoles = new();
    private readonly HashSet<MusicalRole> _armedRoles = new();

    private void Awake()
    {
        _spawnTime = Time.time;
        if (!soloVoice) soloVoice = FindAnyObjectByType<SoloVoice>();
        if (!dustWaveDriver) dustWaveDriver = FindAnyObjectByType<SuperNodeCosmicDustWaveDriver>();
        ResolveDrumTrackIfNeeded();
    }

    public void Initialize(SoloVoice sv, DrumTrack drum, InstrumentTrack track = null)
    {
        soloVoice = sv;
        drumTrack = drum;

        // Backward-compatible path: if a single track is passed, treat it as one active role.
        _trackByRole.Clear();
        _activeRoles.Clear();
        _armedRoles.Clear();

        if (track != null)
        {
            _trackByRole[track.assignedRole] = track;
            _activeRoles.Add(track.assignedRole);
        }
        else
        {
            BuildRoleMapFromController();
        }
    }

    private void OnEnable()
    {
        _spawnTime = Time.time;
        _sawFirstBoundary = false;
        _commitPending = false;
        _resolvedFired = false;

        if (_activeRoles.Count == 0)
            BuildRoleMapFromController();

        TrySubscribeToBoundary();
    }

    private void OnDisable()
    {
        if (drumTrack != null)
            drumTrack.OnLoopBoundary -= OnLoopBoundary;
    }

    private void ResolveDrumTrackIfNeeded()
    {
        if (drumTrack != null) return;
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.activeDrumTrack != null) drumTrack = gfm.activeDrumTrack;
        else drumTrack = FindAnyObjectByType<DrumTrack>();
    }

    private void BuildRoleMapFromController()
    {
        _trackByRole.Clear();
        _activeRoles.Clear();
        _armedRoles.Clear();

        var tracks = ResolveTracks();
        if (tracks == null) return;

        foreach (var t in tracks)
        {
            if (t == null || t.assignedRole == MusicalRole.None) continue;
            _trackByRole[t.assignedRole] = t;
            _activeRoles.Add(t.assignedRole);
        }
    }

    private void TrySubscribeToBoundary()
    {
        ResolveDrumTrackIfNeeded();
        if (drumTrack == null) return;

        drumTrack.OnLoopBoundary -= OnLoopBoundary;
        drumTrack.OnLoopBoundary += OnLoopBoundary;
    }

    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (Time.time - _spawnTime < spawnCollisionGraceSeconds) return;

        var vehicle =
            (collision.collider != null ? collision.collider.GetComponentInParent<Vehicle>() : null)
            ?? (collision.rigidbody != null ? collision.rigidbody.GetComponent<Vehicle>() : null);

        if (vehicle == null) return;
        if (collision.relativeVelocity.magnitude < minImpactSpeed) return;

        if (!TryArmNextRole(out var armedRole))
            return;

        if (playSafeCollisionAudio)
            PlaySafeCollisionFeedback(armedRole);

        EmitRoleWave(armedRole, collision.GetContact(0).point);

        // Defer all musical commit until loop boundary.
        _commitPending = true;
    }

    private bool TryArmNextRole(out MusicalRole role)
    {
        role = MusicalRole.None;
        if (_activeRoles.Count == 0) return false;

        foreach (var r in _activeRoles)
        {
            if (_armedRoles.Contains(r)) continue;
            _armedRoles.Add(r);
            role = r;
            return true;
        }

        return false;
    }

    private void PlaySafeCollisionFeedback(MusicalRole armedRole)
    {
        // Adapter: intentionally non-harmonic-safe feedback.
        // Uses drum-adjacent neutral tick via SoloVoice if available.
        // Replace with dedicated one-shot bus if your project already has it.
        if (soloVoice == null) return;

        int neutralNote = 60; // C4 as neutral placeholder; short & quiet to reduce harmonic implication.
        soloVoice.PlayImmediateQuantizedLickFromTracks(Enumerable.Empty<InstrumentTrack>(), 0);

        // Optional explicit track-local ping if role track exists and team prefers it:
        if (_trackByRole.TryGetValue(armedRole, out var t) && t != null)
            t.PlayNote127(neutralNote, collisionAudioTicks, collisionAudioVelocity);
    }

    private void EmitRoleWave(MusicalRole role, Vector2 worldPos)
    {
        if (dustWaveDriver == null) return;
        if (!_trackByRole.TryGetValue(role, out var t) || t == null) return;

        dustWaveDriver.EmitRoleWave(worldPos, role, t.trackColor);
    }

    private void OnLoopBoundary()
    {
        if (!_sawFirstBoundary)
        {
            _sawFirstBoundary = true;
            if (despawnOnNextBoundary && !_commitPending)
            {
                FireResolvedOnce();
                Destroy(gameObject);
            }
            return;
        }

        if (!_commitPending)
            return;

        CommitArmedRolesAndResolve();
    }

    private void CommitArmedRolesAndResolve()
    {
        foreach (var kv in _trackByRole)
        {
            var role = kv.Key;
            var track = kv.Value;
            if (track == null) continue;

            if (_armedRoles.Contains(role))
            {
                track.RetuneLoopToCurrentProgression();
            }
            else
            {
                track.BeginNewMotifHardClear("SuperNodeUnarmedClear");
            }
        }

        if (dustWaveDriver != null)
            dustWaveDriver.ResetSuperNodeOverlays();

        FireResolvedOnce();
        Destroy(gameObject);
    }

    private void FireResolvedOnce()
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        OnResolved?.Invoke();
    }

    private IEnumerable<InstrumentTrack> ResolveTracks()
    {
        var gfm = GameFlowManager.Instance;
        var ctrl = gfm != null ? gfm.controller : null;
        return (ctrl != null) ? ctrl.tracks : null;
    }
}
