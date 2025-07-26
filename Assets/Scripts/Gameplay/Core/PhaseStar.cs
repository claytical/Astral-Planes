using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using UnityEngine;
using Random = UnityEngine.Random;

public static class ShardColorUtility
{
    // 🎨 Base palettes for utility categories
    private static readonly Color MoodShiftColor = new Color(1f, 0.7f, 0.9f);      // pastel rose
    private static readonly Color StructureShiftColor = new Color(0.5f, 0.9f, 1f);  // cyan-teal
    private static readonly Color UnknownColor = Color.gray;
    public static Color RoleColor(MusicalRole role)
    {
        return role switch
        {
            MusicalRole.Groove => new Color(0.4f, 0.6f, 1f),
            MusicalRole.Harmony => new Color(1f, 0.5f, 0.8f),
            MusicalRole.Bass => new Color(0.7f, 0.5f, 1f),
            MusicalRole.Lead => new Color(1f, 0.8f, 0.3f),
            _ => Color.gray
        };
    }
}

public class PhaseStar : MonoBehaviour
{
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
    private List<DiamondVisual> diamonds = new();
    private bool isDestroyed = false;
    private bool canBeHit = true;
    private bool isDepleted = false;
    private bool collapseStarted = false;
    private int orbitCount = 0;
    private Collider2D starCollider;
    private ParticleSystem.MainModule starParticlesMain;
    private MusicalPhase assignedPhase;
    private void BeginEntrance()
    {
        StartCoroutine(EntranceRoutine());
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

        SpriteRenderer sr = GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            Color c = sr.color;
            c.a = 0f;
            sr.color = c;
        }

        progressionManager.phaseStarActive = true;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            // Scale and fade in
            transform.localScale = Vector3.Lerp(startScale, endScale, Mathf.SmoothStep(0, 1, t));

            if (sr != null)
            {
                Color c = sr.color;
                c.a = Mathf.Lerp(0f, 1f, t);
                sr.color = c;
            }

            yield return null;
        }

        if (collider) collider.enabled = true;
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        Vehicle v = coll.gameObject.GetComponent<Vehicle>();
        if (v != null)
        {

            if (isDepleted || !canBeHit) return;
        
            Debug.Log($"Continuing with routine...");

            canBeHit = false;

            CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Aether);
            MusicalPhaseProfile profile = progressionManager.GetProfileForPhase(assignedPhase);
            foreach (var hex in FindObjectsOfType<CosmicDust>())
            {
                hex.ShiftToPhaseColor(profile,2f);
            }

            Vector2 contactPoint = coll.GetContact(0).point;
            StartCoroutine(ImpactResponse(v, contactPoint));
        }
        
    }

    private IEnumerator ImpactResponse(Vehicle vehicle, Vector2 contactPoint)
    {
        if (impactEffectPrefab != null)
        {
            Instantiate(impactEffectPrefab, contactPoint, Quaternion.identity);
        }

        Vector2 bounceDirection = (vehicle.transform.position - transform.position).normalized;
        vehicle.ApplyImpulse(bounceDirection * impactForce);
        Color shardColor = Color.white;

        if (orbitCount < diamonds.Count && diamonds[orbitCount] != null)
        {
            shardColor = diamonds[orbitCount].assignedColor;
            Destroy(diamonds[orbitCount].gameObject);
            diamonds[orbitCount] = null;
        }
        Debug.Log($"Running SpawnColoredMineNode: Orbit Count {orbitCount} Color: {shardColor}" );
        SpawnColoredMineNode(orbitCount, contactPoint, shardColor);
        orbitCount++;

        if (orbitCount == hitsRequired - 2)
        {
            if (diamonds[hitsRequired - 2] != null) diamonds[hitsRequired - 2].RotateToCross();
            if (diamonds[hitsRequired - 1] != null) diamonds[hitsRequired - 1].RotateToCross();
        }

        if (orbitCount >= hitsRequired && !collapseStarted)
        {
            Debug.Log($"Running coroutine to start collapse event");
            isDepleted = true;
            collapseStarted = true;
            StartCoroutine(StartCollapseEvent());
        }
        Debug.Log($"Orbit Count {orbitCount} Hits Required: {hitsRequired} Collapse Started: {collapseStarted}");
        yield return new WaitForSeconds(hitCooldown);
        Debug.Log($"Phase Star can be hit.");
        canBeHit = true;
    }

        private IEnumerator StartCollapseEvent()
    {
        Debug.Log($"Starting collapse event...");
        if (starCollider != null)
        {
            starCollider.enabled = false;
        }

        float t = 0f;
        while (drumTrack.GetRemainingMineNodeCount() > 0)
        {
            t += Time.deltaTime / gravityRampDuration;
            if (t > 1f)
            {
                Debug.Log($"⏳ Still waiting on MineNodes: {drumTrack.GetRemainingMineNodeCount()}");
                t = 0f;
            }
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

        if (drumTrack != null)
        {
            drumTrack.FinalizeCurrentPhaseSnapshot();
            RemixTracksForDarkStar();
            if (drumTrack.galaxyVisualizer != null)
            {
                var snapshot = drumTrack.sessionPhases.LastOrDefault();
                if (snapshot != null)
                {
                    drumTrack.galaxyVisualizer.AddSnapshot(snapshot);
                }
            }
        }
        
        if (drumTrack != null && drumTrack.galaxyVisualizer != null)
        {
            var snapshot = drumTrack.BuildCurrentPhaseSnapshot();
        }

        if (collapsePrefab != null)
        {
            Instantiate(collapsePrefab, transform.position, Quaternion.identity);
        }        
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

            set.Initialize(track.GetTotalSteps());
            track.ClearLoopedNotes(TrackClearType.Remix);

            var steps = set.GetStepList();
            var notes = set.GetNoteList();

            for (int i = 0; i < Mathf.Min(4, steps.Count); i++)
            {
                int step = steps[i];
                int note = set.GetNextArpeggiatedNote(step);
                int dur = track.CalculateNoteDuration(step, set);
                float vel = Random.Range(60f, 100f);
                track.GetPersistentLoopNotes().Add((step, note, dur, vel));
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

    }

    private void FixedUpdate()
    {
        if (!isDepleted) return;
        // Once the star has become a black hole, apply passive pull (if desired)
        // If active pull during StartCollapseEvent is sufficient, this can remain empty
    }



    void Awake()
    {
        starCollider = GetComponent<Collider2D>();
        starParticlesMain = particleSystem.main;
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
        Debug.Log($"Getting Mined Object Directive for Mine Node {index} / {hitsRequired}. Using {spawnStrategyProfile}");

        var directive = spawnStrategyProfile.GetMinedObjectDirective(drumTrack.trackController);
        Debug.Log($"Directive: {directive}");
        if (directive == null) return;

        Vector2Int cell = drumTrack.GetRandomAvailableCell();
        if (cell.x == -1) return;

        Vector3 targetPos = drumTrack.GridToWorldPosition(cell);
        Debug.Log($"Spawning wrapper...");
        GameObject wrapper = Instantiate(mineNodeSpawnerPrefab, spawnFrom, Quaternion.identity);
        MineNodeSpawner spawner = wrapper.GetComponent<MineNodeSpawner>();
        if (spawner == null) return;
        Debug.Log($"Configuring Spawner...");

        spawner.SetDrumTrack(drumTrack);
        MineNode mineNode = spawner.SpawnNode(cell, directive);
        mineNode.gameObject.SetActive(false);
        if (mineNode != null)
        {
            Debug.Log($"Moving shard {wrapper.name} from {spawnFrom} to {targetPos}");
            StartCoroutine(MoveShardToTarget(spawnFrom, targetPos, wrapper, mineNode.gameObject, color));
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
            set.Initialize(track.GetTotalSteps());

            track.ClearLoopedNotes(TrackClearType.Remix);

            var steps = set.GetStepList();
            var notes = set.GetNoteList();

            for (int i = 0; i < Mathf.Min(6, steps.Count); i++)
            {
                int step = steps[i];
                int note = set.GetNextArpeggiatedNote(step);
                int dur = track.CalculateNoteDuration(step, set);
                float vel = Random.Range(50f, 90f);
                track.GetPersistentLoopNotes().Add((step, note, dur, vel));
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
