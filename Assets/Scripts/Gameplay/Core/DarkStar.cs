using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Random = UnityEngine.Random;

public class DarkStar : MonoBehaviour
{
    public ParticleSystem dangerParticles;
    public GameObject starShardPrefab;
    public SpriteRenderer heart;
    public int maxHits = 4; // Or make this configurable
    public Color safeColor = Color.white;
    public GameObject ghostDiamondPrefab;
    public MusicalPhase currentPhase = MusicalPhase.Establish;

    private bool isDangerous = false;
    private int currentHits = 0;
    private List<GameObject> orbitingStarShards = new(); // Shard visuals
    private MineNodeProgressionManager progressionManager;
    private DrumTrack drumTrack;
    private bool isActive;
    private bool isInCollapsePhase;
    private Coroutine movementRoutine;
    private MusicalRole currentRole;
    private int successfulHits = 0;
    private int totalRolesProcessed = 0;
    private enum DarkStarState { Idle, Charging, Evading, Collapse }
    private DarkStarState currentState = DarkStarState.Idle;
    private Coroutine finalPulseRoutine;

    private List<MusicalRole> roleEjectionCycle = new List<MusicalRole>
    {
        MusicalRole.Groove,
        MusicalRole.Lead,
        MusicalRole.Bass,
        MusicalRole.Harmony
    };
    private enum DarkStarBattleState
    {
        Ejecting,
        Vulnerable,
        Collapsing,
        Complete
    }

    private DarkStarBattleState battleState = DarkStarBattleState.Ejecting;
    
    public void Initialize(MineNodeProgressionManager manager)
    {
        progressionManager = manager;
        drumTrack = progressionManager.GetComponent<DrumTrack>();
    }
    public void Begin()
    {
        successfulHits = 0;
        totalRolesProcessed = 0;
        Color color = safeColor;
        color.a = .2f;
        safeColor = color;
        Vector2Int centerCell = drumTrack.WorldToGridPosition(transform.position);
        drumTrack.hexMazeGenerator.ClearMaze();
        drumTrack.hexMazeGenerator.GenerateMaze(centerCell, drumTrack.currentPhase, progressionManager.GetHollowRadiusForCurrentPhase(), true);
        //FindObjectOfType<GlitchManager>()?.EnableAllGlitchesSubtle();
        Debug.Log("[DarkStar] Begin()");
        isActive = true;
        drumTrack.EnterDarkStarDrumLoop();
        transform.position = progressionManager.GetDarkStarSpawnPoint();
        currentRole = MusicalRole.Bass;
        SetMovementMode(currentRole);
        StartNextEjection();
        for (int i = 0; i < maxHits; i++)
        {
            float angle = (360f / maxHits) * i;

            GameObject shard = Instantiate(starShardPrefab, transform);
            shard.transform.localRotation = Quaternion.Euler(0, 0, angle); // optional: align visually

            var rotator = shard.GetComponent<RotateConstant>();
            if (rotator != null)
            {
                rotator.rotationMode = RotationMode.Swirl;
                rotator.baseSpeed = Random.Range(90f, 150f);
                rotator.shardIndex = i;
                rotator.totalShards = maxHits;
                rotator.ApplyRotationSettings();
            }
            orbitingStarShards.Add(shard);
        }
    }
    private void FinalStrike(Vehicle vehicle)
    {
        if (battleState != DarkStarBattleState.Collapsing) return;
        StartCoroutine(FlashHeart(Color.red, .5f));
        Debug.Log("💫 Final strike successful — ending DarkStar phase.");
        battleState = DarkStarBattleState.Complete;

        Vector2 dir = (vehicle.transform.position - transform.position).normalized;
        vehicle.ApplyImpulse(dir * 25f);

        // End the phase right now — skip ghost storm
        EndDarkStarPhase();
    }
    private IEnumerator FlashHeart(Color flashColor, float duration)
    {
        Color original = heart.color;
        heart.color = flashColor;
        yield return new WaitForSeconds(duration);
        heart.color = original;
    }


    private void SetDangerState(bool danger)
    {
        isDangerous = danger;

        if (danger)
        {
            if (dangerParticles.isPlaying)
                dangerParticles.Stop();
            // Optional: ensure it’s cleared
            dangerParticles.Clear();
            heart.enabled = true;
        }
        else
        {
            dangerParticles.Play();
            heart.enabled = false;
        }
    }
    private void SpawnGhostDiamond(MusicalRole role, int step, int note, float velocity, int duration = 2, InstrumentTrack source = null)
    {
        if (ghostDiamondPrefab == null) return;
        float angle = Random.Range(0f, 360f);
        float radius = 1.5f;
        Vector3 offset = Quaternion.Euler(0, 0, angle) * Vector3.right * radius;
        GameObject ghost = Instantiate(ghostDiamondPrefab, transform);
        ghost.transform.position += offset;
        DiamondGhost dg = ghost.GetComponent<DiamondGhost>();

        if (dg != null)
        {
            Color roleColor = ShardColorUtility.RoleColor(role);
            dg.sourceTrack = source;
            dg.stepIndex = step;
            dg.velocity = velocity;
            dg.noteDuration = duration;
            dg.midiNote = note;

            dg.InitializeWithColor(roleColor, angle, radius, role, transform.position);
            dg.Eject(); // immediately launch outward
        }
    }
    private IEnumerator CollapseCountdown(float loopDuration)
    {
        Debug.Log("🌌 Collapse phase begins. Hit the core or face destruction.");
        isInCollapsePhase = true;
        battleState = DarkStarBattleState.Collapsing;
        finalPulseRoutine = StartCoroutine(PulseFinalStrikeParticles());
        SetDangerState(false);     // No longer ejecting notes
        
        float timer = 0f;
        bool finalStrikeLanded = false;
         
        while (timer < loopDuration)
        {
            timer += Time.deltaTime;

            foreach (var vehicle in GameFlowManager.Instance.GetAllVehicles())
            {
                if (vehicle == null) continue;

                // Pull all vehicles inward
                Vector2 pull = (transform.position - vehicle.transform.position).normalized * 10f * Time.deltaTime;
                vehicle.ApplyGravitationalForce(pull);
            }

            // Check for final strike
            if (battleState != DarkStarBattleState.Collapsing)
            {
                finalStrikeLanded = true;
                break;
            }

            yield return null;
        }

        if (!finalStrikeLanded)
        {
            Debug.Log("💀 Collapse failed – unleashing ghost storm.");
            StartCoroutine(FlashHeart(Color.black, 0.5f));
            TriggerCollapseGhostStorm();
            yield return new WaitForSeconds(loopDuration);
        }

        EndDarkStarPhase();
    }
    private IEnumerator FadeParticles(bool fadeIn, float duration = 1f)
    {
        if (dangerParticles == null) yield break;

        var main = dangerParticles.main;
        float time = 0f;

        // Get initial alpha
        float startAlpha = fadeIn ? 0f : main.startColor.color.a;
        float endAlpha = fadeIn ? 1f : 0f;

        Color baseColor = main.startColor.color;

        while (time < duration)
        {
            float t = time / duration;
            float alpha = Mathf.Lerp(startAlpha, endAlpha, t);
            Color newColor = baseColor;
            newColor.a = alpha;

            main.startColor = newColor;

            time += Time.deltaTime;
            yield return null;
        }

        // Set final color
        baseColor.a = endAlpha;
        main.startColor = baseColor;

        // Optionally stop or start depending on direction
        if (!fadeIn) dangerParticles.Stop();
        else if (!dangerParticles.isPlaying) dangerParticles.Play();
    }

    private void TriggerCollapseGhostStorm()
    {
        Debug.Log("☠️ Collapse missed — ghost storm unleashed.");
        isInCollapsePhase = false;

        int ghostCount = 16; // adjust for mayhem

        for (int i = 0; i < ghostCount; i++)
        {
            MusicalRole role = roleEjectionCycle[Random.Range(0, roleEjectionCycle.Count)];
            float angle = Random.Range(0f, 360f);
            float radius = 2.5f;
            Vector3 offset = Quaternion.Euler(0, 0, angle) * Vector3.right * radius;
            Vector3 spawnPos = transform.position + offset;

            GameObject ghost = Instantiate(ghostDiamondPrefab, spawnPos, Quaternion.identity, null);
            DiamondGhost dg = ghost.GetComponent<DiamondGhost>();

            if (dg != null)
            {
                Color roleColor = ShardColorUtility.RoleColor(role);
                dg.InitializeWithColor(roleColor, angle, radius);
                dg.Eject(powerMultiplier: 2f); // brutal velocity
            }
        }

        StartCoroutine(SelfDestructAfterStorm());
    }
    private IEnumerator SelfDestructAfterStorm()
    {
        yield return new WaitForSeconds(2f); // delay lets ghosts get going

        EndDarkStarPhase(); // or leave ghosts active if you're mean 😈
    }
    public Vector2Int GetRandomAvailableCell()
    {
        return drumTrack.GetRandomAvailableCell();
    }
    public Vector3 GridToWorldPosition(Vector2Int cell)
    {
        return drumTrack.GridToWorldPosition(cell);
    }

    private void SetMovementMode(MusicalRole role)
    {
        if (movementRoutine != null) StopCoroutine(movementRoutine);
        movementRoutine = StartCoroutine(MovementBehaviorLoop());
    }
    
    private void UpdateVisualState(bool showParticles, bool showSprites)
    {
        if (dangerParticles != null)
        {
            if (showParticles && !dangerParticles.isPlaying)
                dangerParticles.Play();
            else if (!showParticles && dangerParticles.isPlaying)
                dangerParticles.Stop();
        }

        foreach (var sr in GetComponentsInChildren<SpriteRenderer>())
        {
            sr.enabled = showSprites;
        }
    }

    private IEnumerator MovementBehaviorLoop()
    {
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        float moveSpeed = 5f;
        float retreatSpeed = 3f;
        float idleTime = 1.0f;

        while (true)
        {
            switch (battleState)
            {
                case DarkStarBattleState.Collapsing:
                    currentState = DarkStarState.Collapse;
                    rb.linearVelocity = Vector2.zero;
                    UpdateVisualState(true, false);
                    break;

                case DarkStarBattleState.Vulnerable:
                    currentState = DarkStarState.Idle;
                    rb.linearVelocity = Vector2.zero;
                    UpdateVisualState(true, false);
                    break;

                case DarkStarBattleState.Ejecting:
                    if (isDangerous)
                    {
                        currentState = DarkStarState.Charging;
                        Transform target = FindNearestVehicle();
                        if (target != null)
                        {
                            Vector2 dir = (target.position - transform.position).normalized;
                            rb.linearVelocity = dir * moveSpeed;
                        }
                    }
                    else
                    {
                        currentState = DarkStarState.Evading;
                        Transform target = FindNearestVehicle();
                        if (target != null)
                        {
                            Vector2 away = (transform.position - target.position).normalized;
                            rb.linearVelocity = away * retreatSpeed;
                        }
                    }
                    UpdateVisualState(false, true);
                    break;
            }

            yield return new WaitForSeconds(idleTime);
        }
    }
    private IEnumerator ChargeBurstRoutine()
    {
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        while (isDangerous && !isInCollapsePhase)
        {
            Transform target = FindNearestVehicle();
            if (target != null)
            {
                Vector2 dir = (target.position - transform.position).normalized;
                rb.AddForce(dir * 10f, ForceMode2D.Impulse);
            }
            yield return new WaitForSeconds(3f);
        }
    }

    private Transform FindNearestVehicle()
    {
        float closest = float.MaxValue;
        Transform nearest = null;

        foreach (var vehicle in GameFlowManager.Instance.GetAllVehicles())
        {
            if (vehicle == null) continue;
            float dist = Vector2.Distance(transform.position, vehicle.transform.position);
            if (dist < closest)
            {
                closest = dist;
                nearest = vehicle.transform;
            }
        }

        return nearest;
    }

    private void OnCollisionEnter2D(Collision2D collision)
    {
        var vehicle = collision.gameObject.GetComponent<Vehicle>();
        if (vehicle == null) return;

        switch (battleState)
        {
            case DarkStarBattleState.Vulnerable:
                HandleShardStrike(vehicle);
                break;

            case DarkStarBattleState.Collapsing:
                FinalStrike(vehicle);
                break;

            case DarkStarBattleState.Ejecting:
                vehicle.ConsumeEnergy(10f); // hit during danger
                break;
        }

        if (collision.gameObject.TryGetComponent<HexagonShield>(out var hex))
        {
            drumTrack.UnregisterHexagon(collision.gameObject);
            Vector2 dir = (collision.transform.position - transform.position).normalized;
            GetComponent<Rigidbody2D>()?.AddForce(dir * 20f, ForceMode2D.Impulse);
            hex.BreakHexagon(CollectionEffectType.MazeToxic);
        }
    }
    private void HandleShardStrike(Vehicle vehicle)
    {
        if (orbitingStarShards.Count > 0)
        {
            var shard = orbitingStarShards[0];
            orbitingStarShards.RemoveAt(0);
            Destroy(shard);

            Vector2 dir = (vehicle.transform.position - transform.position).normalized;
            vehicle.CollectEnergy(10);
            vehicle.ApplyImpulse(dir * 20f);

            successfulHits++;
            if (successfulHits >= maxHits)
            {
                StartCoroutine(CollapseCountdown(drumTrack.GetLoopLengthInSeconds()));
                return;
            }

            SetDangerState(true); // ✅ MAKE IT DANGEROUS IMMEDIATELY
            SetMovementMode(currentRole); // ✅ Restart movement after vulnerability
            StartNextEjection();  // Which starts the next ejection phase
        }
    }

    private IEnumerator PulseFinalStrikeParticles(float duration = 5f, float pulseSpeed = 2f)
    {
        if (dangerParticles == null) yield break;

        var main = dangerParticles.main;
        Color baseColor = main.startColor.color;

        float timer = 0f;

        if (!dangerParticles.isPlaying)
            dangerParticles.Play();

        while (timer < duration && battleState == DarkStarBattleState.Collapsing)
        {
            float alpha = (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f; // 0–1
            Color newColor = baseColor;
            newColor.a = Mathf.Lerp(0.2f, 1f, alpha); // optional min/max
            main.startColor = newColor;

            timer += Time.deltaTime;
            yield return null;
        }

        baseColor.a = 0f;
        main.startColor = baseColor;
        dangerParticles.Stop();
    }

    private void StartNextEjection()
    {
        if (battleState == DarkStarBattleState.Collapsing || battleState == DarkStarBattleState.Complete)
            return;

        if (totalRolesProcessed >= roleEjectionCycle.Count)
        {
            Debug.Log("🛑 All roles processed, entering collapse phase.");
            StartCoroutine(CollapseCountdown(drumTrack.GetLoopLengthInSeconds()));
            return;
        }

        MusicalRole role = roleEjectionCycle[totalRolesProcessed];
        totalRolesProcessed++;
        battleState = DarkStarBattleState.Ejecting;
        SetDangerState(true);
        StartCoroutine(EjectAllNotesThenBecomeVulnerable(role));
    }
    private IEnumerator EjectAllNotesThenBecomeVulnerable(MusicalRole role)
    {
        InstrumentTrack track = drumTrack.trackController.FindTrackByRole(role);
        if (track == null) yield break;
        if (track.GetNoteDensity() == 0)
        {
            var noteSet = track.GetCurrentNoteSet();
            if (noteSet != null)
            {
                noteSet.Initialize(track.GetTotalSteps());
                int step = Random.Range(0, track.GetTotalSteps());
                int note = noteSet.GetNextArpeggiatedNote(step);
                int duration = track.CalculateNoteDuration(step, noteSet);
                float velocity = Random.Range(60f, 100f);
                track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
            }
        }

        var notes = new List<(int step, int note, int duration, float velocity)>(track.GetPersistentLoopNotes());

        Debug.Log($"[DarkStar] Checking role {role}, found {notes.Count} notes");
        if (notes.Count == 0) yield break;

        float interval = drumTrack.GetLoopLengthInSeconds() / notes.Count;

        foreach (var (step, note, duration, velocity) in notes)
        {
            SpawnGhostDiamond(role, step, note, velocity, duration, track);
            yield return new WaitForSeconds(interval);
        }
        track.ClearLoopedNotes();

        // Vulnerability window
        SetDangerState(false);
        battleState = DarkStarBattleState.Vulnerable;
        StartCoroutine(FlashHeart(Color.white, .4f));
        Debug.Log("💠 DarkStar is vulnerable – strike now!");
    }

  
    private void EndDarkStarPhase()
    {
        StopCoroutine(finalPulseRoutine);
        foreach (var ghost in FindObjectsOfType<DiamondGhost>())
        {
            if (ghost != null)
            {
                ghost.TryExplode();    // visual feedback
                ghost.DestroySelf();   // clean removal
            }
        }

        isActive = false;
        GameFlowManager.Instance.glitch.ResetGlitches(); // or your preferred disable method
        if (drumTrack != null)
        {
            drumTrack.ExitDarkStarDrumLoop();
            drumTrack.ExitDarkStarMode();
        }

        Debug.Log("\U0001F311 DarkStar complete. Begin silence.");
        progressionManager?.OnDarkStarComplete();
        Destroy(gameObject);
    }

    public Vector3 GetCenter() => transform.position;
}
