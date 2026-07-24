using UnityEngine;

// State drives physics path; Intent drives decision-making and animation.
// Valid combinations: Drifting+Thinking, Drifting+Committing, Fleeing+Escaping.
// A node in Fleeing always has Intent=Escaping. A Drifting node may be Thinking or Committing.
// Mismatch (e.g. Drifting+Escaping) is representable but has undefined behavior.
public enum DiscoveryTrackNodeState { Drifting, Fleeing }
public enum DiscoveryTrackNodeBehaviorIntent { Thinking, Committing, Escaping }
public enum DiscoveryTrackNodeOutcome { Captured, Escaped, Expired }

public partial class DiscoveryTrackNode : TrackNode
{
    public SpriteRenderer coreSprite;
    public SpriteRenderer outlineSprite;
    [SerializeField] private DiscoveryTrackNodeConfig config;

    [Header("Grid Containment")]
    [SerializeField] private bool debugSweepContainment = false;

    [Header("Containment Ownership Debug")]
    [SerializeField] private bool assertSingleHardCorrectionPerTick = true;
    private int _hardCorrectionsThisTick;

    public bool didContainmentThisTick { get; private set; }

    public event System.Action<DiscoveryTrackNode, DiscoveryTrackNodeOutcome> OnResolved;
    public event System.Action<DiscoveryTrackNodeBehaviorIntent> OnBehaviorIntentChanged;

    public bool WasCaptured { get; private set; }
    public bool WasEscaped  { get; private set; }
    public DiscoveryTrackNodeState State { get; private set; } = DiscoveryTrackNodeState.Drifting;
    public DrumTrack DrumTrack => _drumTrack;
    public DiscoveryTrackNodeLocomotionProfile ActiveLocomotionProfile => _activeLocomotionProfile;

    private Vector2Int _spawnCell;
    private float _nextEscapeAllowedTime = 0f;
    private int _stepsPerLoop = 16;
    private int _strength;
    private int _maxStrength;
    private Vector3 _originalScale;
    private Color _lockedColor;
    private bool _depletedHandled;
    private Collider2D _col;
    private Rigidbody2D _rb;
    private DrumTrack _drumTrack;
    private NoteSet _noteSet;
    private InstrumentTrack _track;
    private MusicalRole _role;
    private float _speed    = 0.5f;
    private DiscoveryTrackNodeLocomotionProfile _activeLocomotionProfile;
    private int _lastProcessedStep = -1;
    private float _rescanTimer = 0f;
    private DiscoveryTrackNodeDustInteractor _dustInteractor;
    private Camera _cam;
    private float _currentDesiredSpeed;
    private bool _hasBeenStruck;
    private Vector2 _prevPhysicsPos;
    private bool _hasPrevPhysicsPos;
    private DiscoveryTrackNodeDecisionArchetypeLibrary.Archetype _decisionArchetype;
    private DiscoveryTrackNodeBehaviorIntent _behaviorIntent = DiscoveryTrackNodeBehaviorIntent.Thinking;
    private CosmicDustGenerator _dustGenerator;
    private DiscoveryTrackNodeBehaviorCategory _behaviorCategory;
    private MusicalRoleProfile _roleProfile;
    private Vehicle _trackedVehicle;

    // Orbital (Harmony): orbit chirality, persists until stall or territory exit
    private int  _orbitSign = 1;
    private bool _orbitSignLocked;

    // Rhythmic (Groove): beat-snapped burst-pause cycle
    private bool  _isInBurst = true;
    private float _burstPauseTimer;
    private bool  _burstPauseSnapPending;
    private float _burstPauseSnapAt;

    // Hit stun: dash-away heading lock after a Vehicle collision, before exit-seeking resumes.
    private float _stunTimer;

    // Fleeing: sought border gap (side columns only — top/bottom are never escape walls)
    private readonly DiscoveryTrackNodeFleeGapFinder _fleeGapFinder = new DiscoveryTrackNodeFleeGapFinder();

    // Escape glide: once escaped, the node stops steering/containment and drifts
    // straight out through the gap until it is fully off-screen.
    private bool    _isEscapeGliding;
    private Vector2 _escapeGlideDir;
    private float   _escapeGlideRadius;
    private const float kEscapeGlideMinSpeed = 1.5f;

    // Motion tunables (previously local consts in FixedUpdate)
    private const float kStallSpeed        = 0.20f; // below this velocity the node is considered stopped
    private const float kStuckDot          = 0.10f; // carve dir vs velocity alignment — catches spinning-in-place
    private const float kEscapeJitterDeg   = 25f;
    private const float kMinSpeedFloor     = 0.25f;
    private const float kStallSamplePeriod = 0.40f; // how often distance is sampled to confirm stall
    private const float kStallDistanceEps  = 0.12f; // minimum travel over sample period to not count as stalled
    private const float kEscapeCooldown    = 0.30f;

    public MusicalRole GetImprintRole() => _role;
    public MusicalRoleProfile RoleProfile => _roleProfile;

    public void Initialize(InstrumentTrack track, NoteSet noteSet, Color tint, Vector2Int spawnCell,
                           Sprite diamondSprite = null)
    {
        _track = track;
        _spawnCell = spawnCell;
        _role = track != null ? track.assignedRole : default;
        _noteSet = noteSet;
        _lockedColor = tint;
        _drumTrack = (track != null) ? track.drumTrack : null;
        // Prefer the motif-selected profile installed on the track; the library keeps only
        // the first-loaded asset per role, so it can't see per-motif tuning.
        var prof = (_track != null && _track.ActiveProfile != null)
            ? _track.ActiveProfile
            : MusicalRoleProfileLibrary.GetProfile(_role);
        if (prof != null) _speed = prof.mineNodeSpeed;  // category speed is applied in FixedUpdateDrifting
        _activeLocomotionProfile = ResolveLocomotionProfile(prof);
        ResolveDecisionArchetype();
        // Strength and expiration resolve through the same locomotion profile that drives speed.
        // NoteSet value wins for expiration only when authored (>0); otherwise the role/archetype
        // baseline stands, falling back to the config default if no profile resolved at all.
        _maxStrength = _activeLocomotionProfile != null ? _activeLocomotionProfile.strength : config.defaultStrength;
        int roleExpire = _activeLocomotionProfile != null ? _activeLocomotionProfile.expireAfterLoops : 0;
        _expireAfterLoops = (noteSet != null && noteSet.expireAfterLoops > 0) ? noteSet.expireAfterLoops
                           : roleExpire > 0 ? roleExpire
                           : config.defaultExpireAfterLoops;
        _roleProfile = prof;
        _behaviorCategory = _role.GetBehaviorCategory();
        _orbitSign = UnityEngine.Random.value < 0.5f ? 1 : -1;
        _orbitSignLocked = false;
        _isInBurst = true;
        _burstPauseTimer = prof?.burstDuration ?? 0.4f;
        _burstPauseSnapPending = false;
        var explode = GetComponent<Explode>();
        if (explode != null) explode.SetTint(_lockedColor, multiply: true);

        float a = UnityEngine.Random.Range(0f, 360f);
        _carveDir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)).normalized;
        _nextDirectionDecisionAt = Time.time + _decisionArchetype.SampleReactionDelay();
        _pathCommitUntil = Time.time + Mathf.Max(0.05f, _decisionArchetype.pathCommitmentDuration * CategoryCommitScale());
        SetBehaviorIntent(DiscoveryTrackNodeBehaviorIntent.Committing);
        _lastProcessedStep = -1;
        SubscribeLoopBoundary(_drumTrack);

        var dust = GetComponent<DiscoveryTrackNodeDustInteractor>();
        if (dust != null) dust.SetLevelAuthority(_drumTrack);
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
        // Register after all fields are initialized so any subscriber that queries this node
        // immediately on registration sees a fully initialized object.
        if (_drumTrack != null)
            _drumTrack.RegisterMineNode(this);
        _dustGenerator = GameFlowManager.Instance?.dustGenerator;
        var vehicles = GameFlowManager.Instance?.GetVehicles();
        _trackedVehicle = (vehicles != null && vehicles.Count > 0) ? vehicles[0] : null;
    }

    protected override bool IsResolvedOrHandled => _depletedHandled;

    private void Start()
    {
        _rb = GetComponent<Rigidbody2D>();
        _dustInteractor = GetComponent<DiscoveryTrackNodeDustInteractor>();
        _originalScale = transform.localScale;
        _strength = _maxStrength;
        Debug.Assert(_track != null && _drumTrack != null && config != null,
            $"[DiscoveryTrackNode] {name} reached Start() without Initialize() or a missing config asset — node will be inert.");
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
        State = DiscoveryTrackNodeState.Fleeing;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[DiscoveryTrackNode] {name} — transitioning to Fleeing.");
    }

    protected override void Expire()
    {
        if (_depletedHandled || ResolvedFired) return;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[DiscoveryTrackNode] {name} — expired after {_loopsSinceSpawn} loops.");

        var explode = GetComponent<Explode>();
        if (explode != null) explode.ExpireExplode();

        if (_dustGenerator != null && _drumTrack != null && config.expireBlastRadiusCells > 0)
        {
            Vector2Int center = _drumTrack.WorldToGridPosition(transform.position);
            _dustGenerator.RevealHiddenDustByRole(center, config.expireBlastRadiusCells, _role);
        }

        FireResolvedOnce();
        StartCoroutine(CleanupAndDestroy());
    }

    public void HandleEscape()
    {
        if (_depletedHandled || ResolvedFired) return;
        WasEscaped = true;
        SetBehaviorIntent(DiscoveryTrackNodeBehaviorIntent.Escaping);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[DiscoveryTrackNode] {name} — escaped through boundary.");
        BeginEscapeGlide();
        FireResolvedOnce();
        StartCoroutine(CleanupAndDestroy(waitForFullEscape: true));
    }

    // Freeze gameplay interaction and drift straight out through the gap so the
    // player watches the node leave instead of it popping at the screen edge.
    private void BeginEscapeGlide()
    {
        float centerX = 0f;
        if (_drumTrack != null && _drumTrack.TryGetPlayAreaWorld(out var area))
            centerX = (area.left + area.right) * 0.5f;
        else if (_cam != null)
            centerX = _cam.transform.position.x;

        _escapeGlideDir    = _rb.position.x >= centerX ? Vector2.right : Vector2.left;
        _escapeGlideRadius = _col != null ? Mathf.Max(_col.bounds.extents.x, _col.bounds.extents.y) : 0f;
        _isEscapeGliding   = true;

        // No capture, bounce, or boundary-trigger interaction on the way out.
        if (_col != null) _col.enabled = false;
        // Edge-hug / escape-push forces would drag the node back toward the wall.
        if (_dustInteractor == null) _dustInteractor = GetComponent<DiscoveryTrackNodeDustInteractor>();
        if (_dustInteractor != null) _dustInteractor.enabled = false;
    }

    // ---------------------------------------------------------------
    // FixedUpdate — state dispatcher
    // ---------------------------------------------------------------

    private void FixedUpdate()
    {
        if (!config.driveCarvingMotionFromNoteSet ||
            _rb == null ||
            _drumTrack == null ||
            _noteSet == null ||
            _track == null)
            return;

        didContainmentThisTick = false;
        _hardCorrectionsThisTick = 0;

        if (_isEscapeGliding)
        {
            // Resolved and leaving: constant outward drift, no steering or containment.
            _rb.linearVelocity = _escapeGlideDir * Mathf.Max(_currentDesiredSpeed, kEscapeGlideMinSpeed);
            return;
        }

        switch (State)
        {
            case DiscoveryTrackNodeState.Drifting: FixedUpdateDrifting(); break;
            case DiscoveryTrackNodeState.Fleeing:  FixedUpdateFleeing();  break;
        }

        _prevPhysicsPos = _rb.position;
        _hasPrevPhysicsPos = true;
    }

    private void OnDisable()
    {
        UnsubscribeLoopBoundary();
    }

    private void OnDestroy()
    {
        ReleaseHeldDustOnce();
        UnsubscribeLoopBoundary();
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
