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

    [Header("Role shard visuals")]
    [SerializeField] private Transform roleShardRoot;
    [SerializeField] private SpriteRenderer templateDiamondRenderer;
    [SerializeField] private float roleShardScale = 0.85f;
    [SerializeField] private float roleShardRotationDegPerSec = 65f;
    [SerializeField] private float highlightedScaleMultiplier = 1.25f;

    public Action OnResolved;

    private float _spawnTime;
    private bool _sawFirstBoundary;
    private bool _resolvedFired;
    private bool _commitPending;

    private readonly Dictionary<MusicalRole, InstrumentTrack> _trackByRole = new();
    private readonly HashSet<MusicalRole> _activeRoles = new();
    private readonly HashSet<MusicalRole> _armedRoles = new();
    private readonly Dictionary<MusicalRole, SpriteRenderer> _roleShardByRole = new();
    private readonly List<MusicalRole> _roleOrder = new();
    private int _highlightedRoleIndex = 0;
    private int _lastDerivedBin = -1;

    private void Awake()
    {
        _spawnTime = Time.time;
        if (!soloVoice) soloVoice = FindAnyObjectByType<SoloVoice>();
        if (!dustWaveDriver) dustWaveDriver = FindAnyObjectByType<SuperNodeCosmicDustWaveDriver>();
        if (templateDiamondRenderer == null) templateDiamondRenderer = GetComponentInChildren<SpriteRenderer>(true);
        if (roleShardRoot == null) roleShardRoot = transform;
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

        RebuildRoleShardVisuals();
        SubscribeStepBoundaries(true);

        TrySubscribeToBoundary();
    }

    private void Update()
    {
        if (_roleShardByRole.Count <= 0) return;
        float delta = roleShardRotationDegPerSec * Time.deltaTime;
        foreach (var kv in _roleShardByRole)
        {
            var sr = kv.Value;
            if (!sr) continue;
            sr.transform.Rotate(0f, 0f, delta, Space.Self);
        }
    }

    private void OnDisable()
    {
        if (drumTrack != null)
            drumTrack.OnLoopBoundary -= OnLoopBoundary;
        SubscribeStepBoundaries(false);
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

        if (_activeRoles.Count == 0)
        {
            var motifRoles = ResolveRolesFromCurrentMotif();
            for (int i = 0; i < motifRoles.Count; i++)
                _activeRoles.Add(motifRoles[i]);
        }
    }

    private List<MusicalRole> ResolveRolesFromCurrentMotif()
    {
        var roles = new List<MusicalRole>();
        var motif = GameFlowManager.Instance?.phaseTransitionManager?.currentMotif;
        if (motif == null) return roles;
        return motif.GetActiveRoles();
    }

    private void RebuildRoleShardVisuals()
    {
        if (templateDiamondRenderer == null || roleShardRoot == null) return;

        foreach (var kv in _roleShardByRole)
            if (kv.Value != null) Destroy(kv.Value.gameObject);
        _roleShardByRole.Clear();
        _roleOrder.Clear();

        var orderedRoles = _activeRoles.Count > 0 ? _activeRoles.ToList() : ResolveRolesFromCurrentMotif();
        orderedRoles = orderedRoles.Where(r => r != MusicalRole.None).Distinct().ToList();
        if (orderedRoles.Count == 0) return;
        _roleOrder.AddRange(orderedRoles);
        _highlightedRoleIndex = 0;

        templateDiamondRenderer.enabled = false;
        int baseOrder = templateDiamondRenderer.sortingOrder;
        for (int i = 0; i < _roleOrder.Count; i++)
        {
            var role = _roleOrder[i];
            var go = new GameObject($"SuperNodeRoleShard_{role}");
            go.transform.SetParent(roleShardRoot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one * roleShardScale;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = templateDiamondRenderer.sprite;
            sr.sortingLayerID = templateDiamondRenderer.sortingLayerID;
            sr.sortingOrder = baseOrder + i;
            sr.sharedMaterial = templateDiamondRenderer.sharedMaterial;
            sr.color = ResolveRoleColor(role);
            _roleShardByRole[role] = sr;
        }

        UpdateShardHighlightVisuals();
    }

    private Color ResolveRoleColor(MusicalRole role)
    {
        var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
        if (roleProfile != null)
        {
            var c = roleProfile.dustColors.baseColor;
            c.a = 1f;
            return c;
        }

        if (_trackByRole.TryGetValue(role, out var track) && track != null)
        {
            var c = track.trackColor;
            c.a = 1f;
            return c;
        }

        return Color.white;
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
        if (_roleOrder.Count == 0) return false;

        role = _roleOrder[Mathf.Clamp(_highlightedRoleIndex, 0, _roleOrder.Count - 1)];
        if (_armedRoles.Contains(role)) return false;

        _armedRoles.Add(role);
        EjectRoleShard(role);
        AdvanceHighlightedRole();
        return true;
    }

    private void EjectRoleShard(MusicalRole role)
    {
        if (_roleShardByRole.TryGetValue(role, out var sr) && sr != null)
            Destroy(sr.gameObject);
        _roleShardByRole.Remove(role);
        _roleOrder.Remove(role);
        if (_roleOrder.Count == 0) _highlightedRoleIndex = 0;
        else _highlightedRoleIndex %= _roleOrder.Count;
        UpdateShardHighlightVisuals();
    }

    private void AdvanceHighlightedRole()
    {
        if (_roleOrder.Count == 0) return;
        _highlightedRoleIndex = (_highlightedRoleIndex + 1) % _roleOrder.Count;
        UpdateShardHighlightVisuals();
    }

    private void UpdateShardHighlightVisuals()
    {
        for (int i = 0; i < _roleOrder.Count; i++)
        {
            var role = _roleOrder[i];
            if (!_roleShardByRole.TryGetValue(role, out var sr) || sr == null) continue;
            bool highlighted = (i == _highlightedRoleIndex);
            sr.transform.localScale = Vector3.one * roleShardScale * (highlighted ? highlightedScaleMultiplier : 1f);
        }
    }

    private void SubscribeStepBoundaries(bool subscribe)
    {
        if (drumTrack == null) return;
        if (subscribe) drumTrack.OnStepChanged += OnStepChanged;
        else drumTrack.OnStepChanged -= OnStepChanged;
    }

    private void OnStepChanged(int stepIndex, int leaderSteps)
    {
        if (_roleOrder.Count <= 1 || leaderSteps <= 0) return;
        int bins = Mathf.Max(1, _roleOrder.Count);
        int stepsPerBin = Mathf.Max(1, leaderSteps / bins);
        int bin = stepIndex / stepsPerBin;
        if (bin != _lastDerivedBin)
        {
            _lastDerivedBin = bin;
            AdvanceHighlightedRole();
        }
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
