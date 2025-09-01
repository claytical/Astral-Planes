using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using UnityEngine;

public static class ShardColorUtility
{
    // 🎨 Base palettes for utility categories
    private static readonly Color MoodShiftColor = new Color(1f, 0.7f, 0.9f);      // pastel rose
    private static readonly Color StructureShiftColor = new Color(0.5f, 0.9f, 1f);  // cyan-teal
    private static readonly Color UnknownColor = Color.gray;
    public static Color RoleColor(MusicalRole role)
    {
        DrumTrack drums = GameFlowManager.Instance.activeDrumTrack;
        return role switch
        {
            MusicalRole.Groove => drums.trackController.FindTrackByRole(MusicalRole.Groove).trackColor,
            MusicalRole.Harmony => drums.trackController.FindTrackByRole(MusicalRole.Harmony).trackColor,
            MusicalRole.Bass => drums.trackController.FindTrackByRole(MusicalRole.Bass).trackColor,
            MusicalRole.Lead => drums.trackController.FindTrackByRole(MusicalRole.Lead).trackColor,
            _ => Color.gray
        };
    }
}

public class PhaseStar : MonoBehaviour
{
    private enum PhaseStarState
    {
        MiningGhosts,       // waiting for GhostPattern harvest to finish
        RemixingTrack,      // actively resolving missed shards for current role
        TransitioningRole,  // waiting on loop boundary / visual beats
        Completed           // done; will destroy & hand off
    }

    private PhaseStarState currentState = PhaseStarState.MiningGhosts;
// NEW: role progression & shard bookkeeping
    private int activeShardsForRole = 0;

// Hook to know when ghost harvest is complete
    private bool harvestComplete = false;
    private bool remixEntered = false;
    [Header("Remix Correction Shards")]
    public GameObject shardPickupPrefab;       // assign a tiny shard prefab with ShardPickup
    public float shardScatterRadius = 1.6f;    // how far from the star to spawn
    public float shardSpawnStagger = 0.03f;    // small delay between spawns (optional)
    public float shardEnergyBase   = 0.06f;    // default energy per shard; phase-tuned
    
    [Header("Dust Interaction")]
    public float dustShrinkRadius = 6f;
    public float dustShrinkUnitsPerSecond = 1.2f;  // how fast each dust radius collapses
    public LayerMask dustLayer;                    // optional: assign to your dust layer for filtering
    public AnimationCurve dustFalloff = AnimationCurve.Linear(0,1, 1,0); // near star = faster
    
    [Header("Star Settings")]
    public int hitsRequired = 8;
    public GameObject diamondVisualPrefab;
    public float hitCooldown = 0.75f;
    public float impactForce = 8f;
    public GameObject impactEffectPrefab;
    public GameObject collapsePrefab;
    public float gravityRampDuration = 3f;
    public float maxGravityForce = 20f;
    public float blackHoleRange = 12f;
    public ParticleSystem particleSystem;
    public float pulseSpeed = 1f;
    public float minAlpha = .1f;
    public float maxAlpha = 1f;

    [SerializeField] private SpawnStrategyProfile spawnStrategyProfile;
    public GameObject mineNodeSpawnerPrefab;

    private DrumTrack drumTrack;
    private MineNodeProgressionManager progressionManager;
    public NoteSetFactory noteSetFactory;
    private List<DiamondVisual> diamonds = new();
    private bool isDestroyed = false;
    private bool canBeHit = true;
    private bool isDepleted = false;
    private bool collapseStarted = false;
    [Header("Impact Ejection (Mining Phase)")]
    public bool ejectMineNodesOnImpact = true;  // turn off if you ever want ghost-only harvest
    public int maxMineNodesPerStar = 6;         // how many we’ll eject via impact
    private int mineNodesEjected = 0;           // current count
    private Collider2D starCollider;
    private ParticleSystem.MainModule starParticlesMain;
    private MusicalPhase assignedPhase;

    private List<MusicalRole> remixOrder = new();
    private int remixIndex = 0;
    private MusicalRole currentRole;
    private MusicalRole currentRemixRole;
    private InstrumentTrackController controller;
    void Awake()
    {
        starCollider = GetComponent<Collider2D>();
        starParticlesMain = particleSystem.main;
    }

    void Start()
    {
        controller = drumTrack.trackController;
        remixOrder = controller.RemixRoles(); // randomized
    }
    
    private void BeginEntrance()
    {
        StartCoroutine(EntranceRoutine());
        BeginMiningPhase(); 
    }
    private void OnGhostHarvestComplete()
    {
        if (currentState != PhaseStarState.MiningGhosts) return;

        harvestComplete = true;

        // Move directly into remix for the first role
        if (remixOrder == null || remixOrder.Count == 0)
        {
            Debug.LogWarning("PhaseStar: remix order empty; completing immediately.");
            TriggerPhaseAdvance();
            return;
        }

        currentState = PhaseStarState.RemixingTrack;
        remixIndex   = 0;
        ApplyRemixRoleVisuals();          // tint to the first role’s color
        SpawnMissedShardsForCurrentRole();// spill missed shards to collect
    }
    private void SpawnMissedShardsForCurrentRole()
{
    if (shardPickupPrefab == null)
    {
        Debug.LogWarning("PhaseStar: shardPickupPrefab not assigned.");
        return;
    }
    if (remixIndex < 0 || remixIndex >= remixOrder.Count)
    {
        Debug.LogWarning("PhaseStar: remixIndex out of range.");
        return;
    }

    MusicalRole role = remixOrder[remixIndex];
    var track = controller.FindTrackByRole(role);
    if (track == null)
    {
        Debug.LogWarning($"PhaseStar: no track found for role {role}; skipping.");
        AdvanceToNextRole();
        return;
    }

    // Query the track for missed ghost payloads (from InstrumentTrack)
    var missed = track.GetMissedGhostPayloads();
    if (missed == null || missed.Count == 0)
    {
        // Nothing to fix for this role → advance immediately
        AdvanceToNextRole();
        return;
    }

    // Phase-aware energy cost
    float energyCost = GetPhaseShardEnergyCost(drumTrack.currentPhase);

    // Color the star to the track color
    if (particleSystem != null)
    {
        var main = particleSystem.main;
        main.startColor = track.trackColor;
    }

    // Spawn wave
    StartCoroutine(SpawnShardWave(track, missed, energyCost));
}
// Called by ShardPickup after it enqueues note placement
    public void NotifyShardResolved()
{
    if (currentState != PhaseStarState.RemixingTrack) return;

    activeShardsForRole = Mathf.Max(0, activeShardsForRole - 1);

    if (activeShardsForRole == 0)
    {
        // (Optional) add a tiny beat to let last marker “seat”
        StartCoroutine(AdvanceAfterBeat());
    }
}

    private IEnumerator AdvanceAfterBeat()
{
    currentState = PhaseStarState.TransitioningRole;

    // Wait to loop boundary if you want; or a short fixed delay feels snappy.
    float delay = drumTrack != null ? Mathf.Min(0.25f, drumTrack.GetLoopLengthInSeconds() * 0.15f) : 0.2f;
    yield return new WaitForSeconds(delay);

    AdvanceToNextRole();
}

    private void AdvanceToNextRole()
{
    remixIndex++;
    if (remixIndex >= remixOrder.Count)
    {
        TriggerPhaseAdvance();
        return;
    }

    currentState = PhaseStarState.RemixingTrack;
    ApplyRemixRoleVisuals();
    SpawnMissedShardsForCurrentRole();
}

    private IEnumerator SpawnShardWave(
    InstrumentTrack track,
    List<(int step, int note, int duration, float velocity)> missed,
    float energyCost)
{
    activeShardsForRole = 0;

    Color tint = track.trackColor; tint.a = 0.9f;

    foreach (var m in missed)
    {
        Vector2 off = UnityEngine.Random.insideUnitCircle * shardScatterRadius;
        Vector3 pos = transform.position + new Vector3(off.x, off.y, 0f);

        var go = Instantiate(shardPickupPrefab, pos, Quaternion.identity);
        var sp = go.GetComponent<ShardPickup>();
        if (sp != null)
        {
            sp.SetOwner(this); // <-- important
            sp.Configure(track, m.step, m.note, m.duration, m.velocity,
                energyCost, PhaseShardFlyDuration(drumTrack.currentPhase));
            sp.ArmNextFixedUpdate(0.01f);
            activeShardsForRole++;       // <-- count up
        }

        var sr = go.GetComponentInChildren<SpriteRenderer>();
        if (sr != null) sr.color = tint;

        if (shardSpawnStagger > 0f)
            yield return new WaitForSeconds(shardSpawnStagger);
    }

    track.ResetGhostPhaseTracking(); // fresh for next role
}

    private IEnumerator EntranceRoutine()
    {
        // Optional: make non-interactable at first
        var collider = GetComponent<Collider2D>();
        if (collider) collider.enabled = false;

        float duration = 2f;
        float elapsed = 0f;
        Vector3 startScale = Vector3.zero;
        Vector3 endScale = transform.localScale;
        transform.localScale = startScale;
        progressionManager.phaseStarActive = true;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            // Scale and fade in
            transform.localScale = Vector3.Lerp(startScale, endScale, Mathf.SmoothStep(0, 1, t));
            yield return null;
        }

        if (collider) collider.enabled = true;
    }
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (currentState == PhaseStarState.Completed) return;
        if (!coll.gameObject.TryGetComponent(out Vehicle vehicle)) return;

        // FX + bounce (keep your feel)
        Vector2 contactPoint = coll.GetContact(0).point;
        Instantiate(impactEffectPrefab, contactPoint, Quaternion.identity);
        Vector2 bounceDirection = (vehicle.transform.position - transform.position).normalized;
        vehicle.ApplyImpulse(bounceDirection * impactForce);

        // --- NEW: eject a MineNode while MINING (no orbit/hitsRequired) ---
        if (currentState == PhaseStarState.MiningGhosts && ejectMineNodesOnImpact)
        {
            if (mineNodesEjected < maxMineNodesPerStar)
            {
                Color shardColor = DetermineMineNodeColor(mineNodesEjected);
                SpawnColoredMineNode(mineNodesEjected, contactPoint, shardColor);
                mineNodesEjected++;
            }

            // if harvest already finished, let this bump kick off remix now
            if (harvestComplete && !remixEntered)
                TryEnterRemix();
        }

        // cooldown/flood protection (reuse your existing gating if present)
        // e.g., if you have canBeHit/hitCooldown, re-arm here after a short wait
    }
    private void TryEnterRemix()
    {
        if (remixEntered) return;
        remixEntered = true;

        if (remixOrder == null || remixOrder.Count == 0)
        {
            TriggerPhaseAdvance();
            return;
        }

        currentState = PhaseStarState.RemixingTrack;
        remixIndex   = 0;
        ApplyRemixRoleVisuals();
        SpawnMissedShardsForCurrentRole();
    }

    private void BeginMiningPhase()
    {
        currentState = PhaseStarState.MiningGhosts;

        // Get randomized remix order up front
        controller = drumTrack.trackController;
        remixOrder = controller.RemixRoles();
        remixIndex = 0;

        // Subscribe to Ghost harvest completion event
        GhostPattern.OnHarvestComplete -= OnGhostHarvestComplete;
        GhostPattern.OnHarvestComplete += OnGhostHarvestComplete;

        // (Optional) particle alpha pulse already in Update()
        // Make star neutral (phase color) while mining
        if (particleSystem != null)
        {
            var main = particleSystem.main;
            main.startColor = MusicalPhaseLibrary.Get(drumTrack.currentPhase).visualColor;
        }
    }

    private float GetPhaseShardEnergyCost(MusicalPhase p)
{
    switch (p)
    {
        case MusicalPhase.Establish: return 0.04f;
        case MusicalPhase.Evolve:    return 0.06f;
        case MusicalPhase.Intensify: return 0.10f;
        case MusicalPhase.Release:   return 0.05f;
        case MusicalPhase.Wildcard:  return 0.12f;
        case MusicalPhase.Pop:       return 0.03f;
        default: return shardEnergyBase;
    }
}

    private float PhaseShardFlyDuration(MusicalPhase p)
{
    switch (p)
    {
        case MusicalPhase.Intensify: return 0.18f;
        case MusicalPhase.Wildcard:  return 0.16f;
        default: return 0.22f;
    }
}

    private void TuneShardForPhase(ShardPickup sp, InstrumentTrack t)
    {
        switch (drumTrack.currentPhase)
        {
            case MusicalPhase.Establish: sp.energyCost = 0.04f; sp.flyToRibbonDuration = 0.22f; break;
            case MusicalPhase.Evolve:    sp.energyCost = 0.06f; break;
            case MusicalPhase.Intensify: sp.energyCost = 0.10f; sp.flyToRibbonDuration = 0.18f; break;
            case MusicalPhase.Release:   sp.energyCost = 0.05f; sp.flyToRibbonDuration = 0.22f; break;
            case MusicalPhase.Wildcard:  sp.energyCost = 0.12f; sp.flyToRibbonDuration = 0.16f; break;
            case MusicalPhase.Pop:       sp.energyCost = 0.03f; sp.flyToRibbonDuration = 0.20f; break;
        }
    }
    
    private void ApplyRemixRoleVisuals()
    {
        if (remixIndex >= remixOrder.Count) return;
        MusicalRole role = remixOrder[remixIndex];
        InstrumentTrack track = controller.FindTrackByRole(role);
        Debug.Log($"Remix Visuals for {track}/{role}");

        if (track != null && particleSystem != null)
        {
            ParticleSystem.MainModule main = particleSystem.main;
            main.startColor = track.trackColor;
        }
    }

    private IEnumerator StartCollapseEvent()
    {
        Debug.Log("🌌 Starting collapse...");
        if (starCollider != null) starCollider.enabled = false;

        float t = 0f;
        while (drumTrack.GetRemainingMineNodeCount() > 0)
        {
            t += Time.deltaTime / gravityRampDuration;
            float pullStrength = Mathf.Lerp(0f, maxGravityForce, t);

            foreach (var vehicle in FindAllVehiclesInRange(blackHoleRange))
            {
                Vector2 toCenter = (transform.position - vehicle.transform.position);
                float dist = Mathf.Max(toCenter.magnitude, 0.5f);
                Vector2 pull = toCenter.normalized * (pullStrength / dist);
                vehicle.ApplyGravitationalForce(pull);
            }

            yield return null;
        }

        drumTrack.FinalizeCurrentPhaseSnapshot();
        RemixTracksForDarkStar();
        if (collapsePrefab != null)
            Instantiate(collapsePrefab, transform.position, Quaternion.identity);

        TriggerPhaseAdvance();
    }

    private void RemixTracksForDarkStar()
    {
        foreach (var track in drumTrack.trackController.tracks)
        {
            if (track.GetNoteDensity() <= 1) continue;

            var set = track.GetCurrentNoteSet();
            if (set == null) continue;

            set.rhythmStyle = RhythmStyle.Dense;
            set.noteBehavior = NoteBehavior.Lead;

            set.Initialize(track, track.GetTotalSteps());
            track.ClearLoopedNotes(TrackClearType.Remix);

            var steps = set.GetStepList();
            var notes = set.GetNoteList();

            for (int i = 0; i < Mathf.Min(4, steps.Count); i++)
            {
                int step = steps[i];
                int note = set.GetNextArpeggiatedNote(step);
                int dur = track.CalculateNoteDuration(step, set);
                float vel = UnityEngine.Random.Range(60f, 100f);
                track.AddNoteToLoop(step, note, dur, vel);
//                track.GetPersistentLoopNotes().Add((step, note, dur, vel));
            }
            
        }
    }

    private List<Vehicle> FindAllVehiclesInRange(float maxRange)
    {
        List<Vehicle> nearbyVehicles = new();
        if (GameFlowManager.Instance == null || GameFlowManager.Instance.localPlayers == null) return nearbyVehicles;

        foreach (var player in GameFlowManager.Instance.localPlayers)
        {
            if (player?.playerVehicle == null) continue;

            float dist = Vector2.Distance(transform.position, player.playerVehicle.transform.position);
            if (dist <= maxRange)
            {
                Vehicle v = player.playerVehicle.GetComponent<Vehicle>();
                if (v != null)
                {
                    nearbyVehicles.Add(v);
                }
            }
        }

        return nearbyVehicles;
    }

    private void Update()
    {
        float alpha = Mathf.Lerp(minAlpha, maxAlpha, 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed));
        Color startColor = starParticlesMain.startColor.color;
        startColor.a = alpha;
        starParticlesMain.startColor = startColor;
        ShrinkNearbyDust();
    }

    private void ShrinkNearbyDust()
    {
        var hits = (dustLayer.value != 0)
            ? Physics2D.OverlapCircleAll(transform.position, dustShrinkRadius, dustLayer)
            : Physics2D.OverlapCircleAll(transform.position, dustShrinkRadius);

        for (int i = 0; i < hits.Length; i++)
        {
            if (!hits[i]) continue;
            if (!hits[i].TryGetComponent<CosmicDust>(out var dust)) continue;

            float d = Vector2.Distance(transform.position, dust.transform.position);
            float t = Mathf.Clamp01(d / Mathf.Max(0.001f, dustShrinkRadius));   // 0 near, 1 at edge
            float rate = dustShrinkUnitsPerSecond * dustFalloff.Evaluate(1f - t);

            dust.ShrinkByPhaseStar(rate);
        }
    }

    public void Initialize(DrumTrack track, SpawnStrategyProfile profile, MusicalPhase phase, MineNodeProgressionManager manager)
    {
        assignedPhase = phase;
        this.drumTrack = track;
        this.progressionManager = manager;
        this.spawnStrategyProfile = profile;
        progressionManager.pendingNextPhase = false;
        hitsRequired = MusicalPhaseLibrary.Get(phase).hitsRequired;

        for (int i = 0; i < hitsRequired; i++)
        {
            GameObject d = Instantiate(diamondVisualPrefab, transform);
            RotateConstant rotator = d.GetComponent<RotateConstant>();
            if (rotator != null)
            {
                rotator.shardIndex = i;
                rotator.rotationMode = MusicalPhaseLibrary.Get(phase).rotationMode;
                rotator.baseSpeed = MusicalPhaseLibrary.Get(phase).rotationSpeed;
            }
            float angle = (360f / hitsRequired) * i;
            d.transform.localPosition = Vector3.zero;
            d.transform.localRotation = Quaternion.Euler(0, 0, angle);

            DiamondVisual visual = d.GetComponent<DiamondVisual>();
            Color logicColor = MusicalPhaseLibrary.Get(phase).visualColor;
            logicColor.a = 50f;
            if (visual != null)
            {
                visual.SetAssignedColor(logicColor);
            }
            else
            {
                Debug.LogWarning($"[PhaseStar] DiamondVisual missing on shard {i}");
            }

            diamonds.Add(visual);
        }
        BeginEntrance();
    }

    private void SpawnColoredMineNode(int index, Vector3 spawnFrom, Color color)
    {
        Debug.Log($"Mine Node {index} / {hitsRequired}. Using {spawnStrategyProfile}");
        if (index >= hitsRequired || spawnStrategyProfile == null) return;

        MusicalPhaseProfile profile = progressionManager.GetProfileForPhase(assignedPhase);

        var directive = spawnStrategyProfile.GetMinedObjectDirective(
            drumTrack.trackController,
            assignedPhase,
            profile,
            drumTrack.minedObjectPrefabRegistry,
            drumTrack.nodePrefabRegistry,
            GameFlowManager.Instance.noteSetFactory // 👈 Inject it from your singleton or manager
        );


        if (directive == null)
        {
            Debug.LogWarning("⚠️ No valid directive returned — skipping spawn.");
            return;
        }

        directive.remixUtility = progressionManager.GetRemixUtility();
        Vector2Int cell = drumTrack.GetRandomAvailableCell();
        if (cell.x == -1) return;

        Vector3 targetPos = drumTrack.GridToWorldPosition(cell);
        GameObject wrapper = Instantiate(mineNodeSpawnerPrefab, spawnFrom, Quaternion.identity);
        MineNodeSpawner spawner = wrapper.GetComponent<MineNodeSpawner>();
        if (spawner == null) return;

        spawner.SetDrumTrack(drumTrack);
        MineNode mineNode = spawner.SpawnNode(cell, directive);
                mineNode.gameObject.SetActive(false); 
    if (mineNode != null)
        {
            var loco = mineNode.GetComponent<MineNodeLocomotion>();
            if (loco != null)
            {
                loco.SetPhaseProvider(() => progressionManager.GetCurrentPhaseName());
            }

            StartCoroutine(MoveShardToTarget(spawnFrom, targetPos, wrapper, mineNode.gameObject, color));
        }
    }

    private Color DetermineMineNodeColor(int index)
    {
        // 1) If you still have diamond visuals, use their assigned color and optionally clear them
        if (diamonds != null && index >= 0 && index < diamonds.Count && diamonds[index] != null)
        {
            var c = diamonds[index].assignedColor;
            // If you want to peel the visual off:
            // Destroy(diamonds[index].gameObject);
            // diamonds[index] = null;
            return c;
        }

        // 2) If you have an active or upcoming remix role, use its track color
        if (controller != null && remixOrder != null && remixOrder.Count > 0)
        {
            int roleIdx = Mathf.Clamp(remixIndex, 0, remixOrder.Count - 1);
            var role = remixOrder[roleIdx];
            var track = controller.FindTrackByRole(role);
            if (track != null) return track.trackColor;
        }

        // 3) Fall back to the phase's visual color
        var phaseProfile = MusicalPhaseLibrary.Get(drumTrack.currentPhase);
        if (phaseProfile != null) return phaseProfile.visualColor;

        // 4) Worst-case
        return Color.white;
    }

    private void EvaluateAllTracks()
    {
        if (drumTrack.trackController == null) return;

        foreach (var track in drumTrack.trackController.tracks)
        {
            float score = track.EvaluateCompositeScore();
            Debug.Log($"🎼 Composite Score for {track.assignedRole}: {score:F2}");
        }
    }


    private IEnumerator MoveShardToTarget(Vector3 start, Vector3 end, GameObject shard, GameObject mineNode, Color fallbackColor)
    {
        float t = 0f;
        float duration = 0.6f;

        // 🌟 Get the shard color from the diamond if available
        Color shardColor = fallbackColor;
        
        // 🎨 Apply color to the moving shard's sprite
        SpriteRenderer shardSprite = shard.GetComponentInChildren<SpriteRenderer>();
        if (shardSprite != null)
        {
            shardSprite.color = shardColor;
        }

        // 🚀 Animate shard toward node
        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            shard.transform.position = Vector3.Lerp(start, end, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }

        // 🪐 Activate and color the MineNode
        if (mineNode != null)
        {
            Debug.Log($"{mineNode.name} moved to {end}");
            mineNode.transform.position = end;
            mineNode.SetActive(true);
        }

        Explode explode = shard.GetComponent<Explode>();
        if (explode != null)
        {
            Debug.Log($"Running Explode on {shard.name}");
            explode.Permanent();
        }
        else
        {
            Debug.Log($"No explode on {shard.name}, destroying instead.");
            Destroy(shard);
        }

    }

        private IEnumerator WaitForRemainingNodes()
    {
        Debug.Log($"⛏ Remaining mine nodes: {drumTrack.GetRemainingMineNodeCount()}");

        progressionManager.phaseLocked = true;

        while (drumTrack != null && drumTrack.GetRemainingMineNodeCount() > 0)
        {
            yield return new WaitForSeconds(0.5f);
        }

        Debug.Log("✅ Mine nodes cleared. Checking darkStarModeEnabled...");
        if (drumTrack.darkStarModeEnabled)
        {
            Debug.Log("Glitch Remix Queued");
//            GlitchRemix();
            float loopLength = drumTrack.GetLoopLengthInSeconds();
            Debug.Log($"🕒 Waiting {loopLength} seconds before Dark Star spawns...");
            yield return new WaitForSeconds(loopLength); // ⏳ Give breathing room
            Debug.Log("🌑 Spawning DarkStar now.");
            progressionManager.SpawnDarkStar(transform.position);
        }
        else
        {
            // Skip DarkStar and trigger phase transition immediately
            Debug.Log("🚀 Calling OnDarkStarComplete()");
            RemixTracksAfterDarkStar();
            progressionManager.SetDarkStarSpawnPoint(transform.position);
            progressionManager.phaseStarActive = false;
            progressionManager.OnDarkStarComplete();
        }

        Destroy(gameObject);
        drumTrack.isPhaseStarActive = true;
    }

        private void RemixTracksAfterDarkStar()
    {
        foreach (var track in drumTrack.trackController.tracks)
        {
            var set = track.GetCurrentNoteSet();
            if (set == null) continue;

            // 🎛 Reset to default behavior for the role
            var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
            if (profile == null) continue;

            set.noteBehavior = profile.defaultBehavior;
            set.rhythmStyle = RhythmStyle.Sparse;
            set.chordPattern = ChordPattern.RootTriad;
            set.Initialize(track, track.GetTotalSteps());

            track.ClearLoopedNotes(TrackClearType.Remix);

            var steps = set.GetStepList();
            var notes = set.GetNoteList();

            for (int i = 0; i < Mathf.Min(6, steps.Count); i++)
            {
                int step = steps[i];
                int note = set.GetNextArpeggiatedNote(step);
                int dur = track.CalculateNoteDuration(step, set);
                float vel = UnityEngine.Random.Range(50f, 90f);
                track.AddNoteToLoop(step, note, dur, vel);
                //track.GetPersistentLoopNotes().Add((step, note, dur, vel));
            }
        }

        drumTrack.trackController.UpdateVisualizer();
    }

        private void TriggerPhaseAdvance()
    {
        Debug.Log("📡 PhaseStar.TriggerPhaseAdvance()");
        if (isDestroyed) return;
        isDestroyed = true;

        if (progressionManager == null)
        {
            Debug.LogWarning("⚠️ PhaseStar has no progressionManager reference.");
            return;
        }
        StartCoroutine(WaitForRemainingNodes());
    }
}
