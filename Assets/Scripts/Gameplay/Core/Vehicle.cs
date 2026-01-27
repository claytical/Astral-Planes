using UnityEngine;
using System.Collections;
using System.Collections.Generic;


public class Vehicle : MonoBehaviour
{
    [SerializeField] private CosmicDustGenerator debugDustGenerator;
    [Header("Boost Carve Tuning")]
    [SerializeField] private float boostCarveRequiredFloor01 = 0.35f;   // cap hardness threshold while boosting
    [SerializeField] private float boostCarveDamageFloor01   = 0.35f;   // ensure low-speed boost can carve at least 1
    public enum DustCarveMode
    {
        NearestResolveDisk,   // current: contact point resolves nearest occupied within radius
        RayMarchLine,         // straight-line tunnel (recommended for “dramatic”)
        Wedge                 // straight line plus slight widening (optional)
    }

    [SerializeField] private DustCarveMode dustCarveMode = DustCarveMode.RayMarchLine;

    public ShipMusicalProfile profile;
    public float capacity = 10f;
    private float _baseBurnAmount = 1f;
    private float _burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure
    private float _dustSfxCooldown = 0f;
    private const float DustSfxInterval = 0.10f; // at most 10 Hz when really grinding dust
    // === Arcade RB2D tuning ===
    [Header("Arcade Movement")]
    [SerializeField] private float arcadeMaxSpeed = 14f;
    [SerializeField] private float arcadeAccel = 40f;
    [SerializeField] private float arcadeBoostAccel = 80f;
    [SerializeField] private float arcadeLinearDamping = 2f;   // typical 0–5
    [SerializeField] private float arcadeAngularDamping = 0.5f; // optional: reduces spin after bumps
    [SerializeField] private bool  requireBoostForThrust = false; // set true if you want “no boost, no thrust”
    [Header("Coast/Stop (mass-dependent)")]
    [SerializeField] private float coastBrakeForce   = 6f;   // N per (m/s). F = -k*v (independent of mass)
    [SerializeField] private float stopSpeed         = 0.05f; // snap-to-rest threshold (m/s)
    [SerializeField] private float stopAngularSpeed  = 5f;   // deg/s
    [SerializeField] private float carveTickHz = 20f; // 20 Hz is plenty
    private float _nextCarveTime;
    private Collider2D _lastDustCollider;
    private float _cumulativeEnergySpent = 0f;
// Single environment scalar (0..1], replaces envSpeedScale/envAccelScale in movement
    [Header("Input Filtering")]
    [SerializeField] float inputDeadzone = 0.20f;   // tune to your stick
    [SerializeField] float inputTimeout  = 0.15f;   // seconds before we auto-zero if Move() isn’t called
    private float _lastEnergyDrainLogTime = -999f;
    private GameFlowManager gfm;
    float   _lastMoveStamp;
    private Vector2 _moveInput;
    private float _boostEnergySpentAccum = 0f;
    public float energyLevel;
    private float difficultyMultiplier = 1f;

    public bool boosting = false;
    private Vector2 _lastNonZeroInput; // remembers the last aim direction
    private float _dustDebugCooldown = 0f;
    private const float DustDebugInterval = 0.5f;

    public GameObject trail; // Trail prefab
    public AudioClip thrustClip;
    
    public PlayerStats playerStatsUI; // Reference to the PlayerStats UI element
    public PlayerStatsTracking playerStats;
    public SpriteRenderer soulSprite;
    private GameObject activeTrail; // Reference to the currently active trail instance 
    private Rigidbody2D rb;
    private AudioManager audioManager;
    private bool isInConstellation = false;
    [SerializeField] private float minAlpha = 0.2f;
    [SerializeField] private float maxAlpha = 1f;
    private SpriteRenderer baseSprite;
    [SerializeField] private bool isLocked = false;
    private Coroutine rainbowRoutine;
    private bool remixTriggeredThisLoop;
    private Vector3 lastPosition;
    private DrumTrack drumTrack;
    private bool incapacitated = false;
    private double loopStartDSPTime;
    private float _lastDamageTime = -1f;
    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;
    [SerializeField] HarmonyDirector harmony;
    [SerializeField] private ShipMusicalProfile shipProfile;
    [Header("Dust Legibility Pocket")]
    [SerializeField] private bool keepDustClearAroundVehicle = true;
    [SerializeField] private int vehicleKeepClearRadiusCells = 1;
    [SerializeField] private float vehicleKeepClearRefreshSeconds = 0.10f;
    private float _nextVehicleKeepClearRefreshAt = 0f;

    private Coroutine _spawnRestPocketCo;

    [Header("Dust Spawn Rest Pocket")]
    [Tooltip("Carves a small pocket at spawn so the vehicle is not born intersecting dust colliders.")]
    [SerializeField] private bool carveSpawnRestPocket = true;
    [Tooltip("If true, compute the pocket radius from the vehicle collider bounds and the drum grid cell size.")]
    [SerializeField] private bool spawnRestPocketAutoRadius = true;
    [Tooltip("Used when Auto Radius is disabled.")]
    [SerializeField] private int spawnRestPocketRadiusCells = 1;
    [Tooltip("Fade time (seconds) for the initial pocket carve.")]
    [SerializeField] private float spawnRestPocketFadeSeconds = 0.05f;
    [Tooltip("Delay (seconds) before carving the pocket. Useful if spawn ordering is tight.")]
    [SerializeField] private float spawnRestPocketDelaySeconds = 0.0f;

    [Header("Scale Calibration (Debug)")] 
    [SerializeField] private bool logScaleCalibrationOnAssign = true;
    private bool _scaleCalibrationLogged = false;
    
    public void ApplyShipProfile(ShipMusicalProfile p, bool refillEnergy = true)
    {
        shipProfile = p;

        // Movement
        arcadeMaxSpeed       = p.arcadeMaxSpeed;
        arcadeAccel          = p.arcadeAccel;
        arcadeBoostAccel     = p.arcadeBoostAccel;
        arcadeLinearDamping  = p.arcadeLinearDamping;
        arcadeAngularDamping = p.arcadeAngularDamping;
        requireBoostForThrust= p.requireBoostForThrust;

        // Coast / Stop / Input
        coastBrakeForce      = p.coastBrakeForce;
        stopSpeed            = p.stopSpeed;
        stopAngularSpeed     = p.stopAngularSpeed;
        inputDeadzone        = p.inputDeadzone;

        // Physics
        rb.mass = p.mass;

        // Fuel tradeoffs (keep your semantics)
        capacity = p.capacity;                    // tank size (you already track capacity/energyLevel)
        _baseBurnAmount *= p.burnEfficiency;       // ship-specific efficiency multiplier
        if (refillEnergy) energyLevel = capacity; // start full on selection

        // Apply damping now
        rb.linearDamping  = arcadeLinearDamping;
        rb.angularDamping = arcadeAngularDamping;
    }
    private void LogScaleCalibrationOnce() { 
        if (_scaleCalibrationLogged || !logScaleCalibrationOnAssign) return; 
        _scaleCalibrationLogged = true;
        gfm = GameFlowManager.Instance; 
        var drums = drumTrack != null ? drumTrack : (gfm != null ? gfm.activeDrumTrack : null); 
        if (drums == null) { 
            Debug.LogWarning($"[Scale] {name}: DrumTrack not assigned yet; cannot report vehicle↔cell scale.", this); 
            return;
        }
        float cell = Mathf.Max(0.0001f, drums.GetCellWorldSize());
        
        // Vehicle size proxy: prefer a CircleCollider2D radius; otherwise use bounds extents.
        float r = 0.5f; 
        var cc = GetComponent<CircleCollider2D>();
        if (cc != null)
            r = cc.radius * Mathf.Abs(transform.lossyScale.x);
        else { 
            var col = GetComponent<Collider2D>(); 
            if (col != null) r = Mathf.Max(col.bounds.extents.x, col.bounds.extents.y);
        } 
        float diameter = r * 2f; 
        float ratio = diameter / cell; // 1.0 means vehicle ~ 1 cell wide
    }

    public float GetMaxSpeed()
    {
        return arcadeMaxSpeed;
    }
    void OnDisable()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || gfm.dustGenerator == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        gfm.dustGenerator.ReleaseVehicleKeepClear(GetInstanceID(), phaseNow);
    }

    private void RefreshVehicleKeepClearIfNeeded()
    {
        if (!keepDustClearAroundVehicle) return;

        // Throttle refresh
        if (Time.time < _nextVehicleKeepClearRefreshAt) return;
        _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleKeepClearRefreshSeconds);

        var gfm = GameFlowManager.Instance;
        if (gfm == null) return;

        var gen = gfm.dustGenerator;
        var drum = gfm.activeDrumTrack;
        if (gen == null || drum == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        int ownerId = GetInstanceID();

        // If not boosting, ensure we RELEASE any previous footprint continuously.
        // (This prevents stale keep-clear claims that permanently veto regrowth.)
        if (!boosting)
        {
            gen.ReleaseVehicleKeepClear(ownerId, phaseNow);
            return;
        }

        // While boosting, maintain the pocket and optionally remove dust.
        Vector2Int centerCell = drum.WorldToGridPosition(rb.position);

        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            Mathf.Max(0, vehicleKeepClearRadiusCells),
            phaseNow,
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: 0.20f
        );
    }

    private IEnumerator Co_CarveSpawnRestPocket()
    {
        // Optional delay to allow spawn ordering (dust grid, drumTrack, etc.) to settle.
        if (spawnRestPocketDelaySeconds > 0f)
            yield return new WaitForSeconds(spawnRestPocketDelaySeconds);
        else
            yield return null; // at least one frame so the dust grid exists

        var gfm = GameFlowManager.Instance;
        if (gfm == null) yield break;

        var gen = gfm.dustGenerator;
        var drum = gfm.activeDrumTrack;
        if (gen == null || drum == null) yield break;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        // Compute which cell we're currently in.
        Vector2 pos = (rb != null) ? rb.position : (Vector2)transform.position;
        Vector2Int centerCell = drum.WorldToGridPosition(pos);

        // Choose a radius that guarantees we are not born overlapping walls.
        int radiusCells = Mathf.Max(0, spawnRestPocketRadiusCells);
        if (spawnRestPocketAutoRadius)
        {
            float cellWorld = Mathf.Max(0.01f, drum.GetCellWorldSize());
            float rWorld = 0.0f;
            var col = GetComponent<Collider2D>();
            if (col != null)
                rWorld = Mathf.Max(col.bounds.extents.x, col.bounds.extents.y);
            else
                rWorld = 0.35f; // conservative fallback

            // Expand by a small margin so resting contacts don't continuously resolve.
            float rWithMargin = rWorld + (cellWorld * 0.15f);
            radiusCells = Mathf.Max(0, Mathf.CeilToInt(rWithMargin / cellWorld));
        }

        // Carve a small pocket *once*, then release keep-clear so regrowth behaves normally.
        // This creates a "rest" volume without creating a tunnel or permanently preventing regrowth.
        int ownerId = GetInstanceID();
        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            radiusCells,
            phaseNow,
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: Mathf.Max(0.01f, spawnRestPocketFadeSeconds)
        );
        gen.ReleaseVehicleKeepClear(ownerId, phaseNow);
    }

    private void OnDestroy()
    {
        var gfm = GameFlowManager.Instance;
        var gen = (gfm != null) ? gfm.dustGenerator : null;
        if (gen == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        gen.ReleaseVehicleKeepClear(GetInstanceID(), phaseNow);
    }
    public void SetColor(Color newColor)
    {
        if (baseSprite != null)
            baseSprite.color = newColor;

    }
    
    void Start()
        {
            rb = GetComponent<Rigidbody2D>();
            gfm = GameFlowManager.Instance;
            var col = GetComponent<Collider2D>();
            Debug.Log(
                $"[VEHICLE:INIT] '{name}' layer={gameObject.layer} " +
                $"col={(col ? col.GetType().Name : "NULL")} col.isTrigger={(col ? col.isTrigger : false)} " +
                $"rb={(rb ? rb.bodyType.ToString() : "NULL")}",
                this
            );
       
            baseSprite = GetComponent<SpriteRenderer>();
            audioManager = GetComponent<AudioManager>();
            loopStartDSPTime = GameFlowManager.Instance.activeDrumTrack.startDspTime;
            ApplyShipProfile(profile);
            if (rb == null)
            {
                Debug.LogError("❌ Rigidbody2D component is missing.");
                enabled = false;
                return;
            }
            if (rb != null)
            {
                rb.gravityScale    = 0f;

                rb.linearDamping  = arcadeLinearDamping;
                rb.angularDamping = arcadeAngularDamping;
                // Safety: ensure we start truly at rest.
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }

            if (audioManager == null)
            {
                Debug.LogError("❌ AudioManager component is missing.");
                enabled = false;
                return;
            }

            if (playerStatsUI == null)
            {
                Debug.LogError("❌ PlayerStats UI reference not assigned.");
                enabled = false;
                return;
            }

            if (playerStats == null)
            {
                Debug.LogError("❌ PlayerStatsTracking component is missing.");
                enabled = false;
                return;
            }

            energyLevel = capacity; // Start at full energy
            lastPosition = transform.position;
            SyncEnergyUI();

            // --- Spawn rest pocket ---
            // The vehicle often spawns inside a solid dust tile by design (teaches boosting),
            // but we still need a tiny free volume so we don't start interpenetrating colliders.
            if (carveSpawnRestPocket)
            {
                if (_spawnRestPocketCo != null) StopCoroutine(_spawnRestPocketCo);
                _spawnRestPocketCo = StartCoroutine(Co_CarveSpawnRestPocket());
            }
        }
    
    public void SyncEnergyUI()
        {
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }

            if (soulSprite != null)
            {
                float alpha = Mathf.Clamp01(energyLevel / capacity);
                Color currentColor = soulSprite.color;
                currentColor.a = alpha;
                soulSprite.color = currentColor;
            }
        }

    public void SetDrumTrack(DrumTrack drums)
        {
            drumTrack = drums;
            LogScaleCalibrationOnce();
        }

    public int GetForceAsDamage()
        {
            float speed = rb.linearVelocity.magnitude;
            float impactCapVelocity = 32f;
            float normalizedSpeed = Mathf.InverseLerp(0f, impactCapVelocity, speed);
            float curvedSpeed = Mathf.Pow(normalizedSpeed, 1.75f);
            float baseDamage = Mathf.Lerp(25f, 100f, curvedSpeed); 

            float massMultiplier = Mathf.Clamp(rb.mass, 0.75f, 2f);
            float damage = baseDamage * massMultiplier;

            // If we hit something within the last 0.5s, pad the damage slightly
            if (Time.time - _lastDamageTime < 0.5f)
            {
                damage = Mathf.Max(damage, 10f); // Floor for quick follow-ups
            }

            _lastDamageTime = Time.time;

            return Mathf.RoundToInt(Mathf.Clamp(damage, 0f, 120f));
        }
    public float GetForceAsMidiVelocity()
    {
        float s2 = rb.linearVelocity.sqrMagnitude;
        float t2 = arcadeMaxSpeed * arcadeMaxSpeed;
        return Mathf.Lerp(80f, 127f, Mathf.InverseLerp(0, t2, s2));
    }

    public void Move(Vector2 direction)
    {
        if (isLocked) return;

        if (direction.magnitude < inputDeadzone) direction = Vector2.zero;

        _moveInput     = direction;
        if (direction.sqrMagnitude > 0f) _lastNonZeroInput = direction.normalized;
        _lastMoveStamp = Time.time;

        // Optional: face input immediately if we’re currently stopped
        if (rb && direction.sqrMagnitude > 0.0001f && rb.linearVelocity.sqrMagnitude < 0.0001f)
        {
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f;
            rb.rotation = angle;
        }
    }

    private void Fly()
        {
            if (trail != null && activeTrail == null)
            {
                activeTrail = Instantiate(trail, transform);
            }

            if (activeTrail != null)
            {
                activeTrail.GetComponent<TrailRenderer>().emitting = true; // Enable the trail's emission
            }
        }
    
    public void TurnOnBoost(float triggerValue)
    {
        
        if (energyLevel > 0 && !boosting)        {
            boosting = true;

            if (audioManager != null && thrustClip != null)
                audioManager.PlayLoopingSound(thrustClip, .5f);
            Fly();
        }
        _burnRateMultiplier = Mathf.Max(0.2f, triggerValue);
    }

    public void TurnOffBoost()
    {
        if (audioManager != null)
        {
            audioManager.StopSound();
        }
        boosting = false;
//        _remixController.ResetRemixVisuals();
        _burnRateMultiplier = 0f; // Reset the multiplier when not boosting

        // Disable the trail's emission when boosting stops
        if (activeTrail != null)
        {
            activeTrail.GetComponent<TrailRenderer>().emitting = false;
        }
    }
    public float GetCumulativeSpentTanks() {
        if (capacity <= 0f) return 0f; 
        return _cumulativeEnergySpent / capacity;
    }
    private void ConsumeEnergy(float amount)
        {
            energyLevel -= amount;
            energyLevel = Mathf.Max(0, energyLevel); // Clamp to 0
            _cumulativeEnergySpent += Mathf.Max(0f, amount);
            if (energyLevel <= 0)
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                    Explode explode = GetComponent<Explode>();
                    if (explode != null)
                    {
                        explode.Permanent();
                    }
                    GameFlowManager.Instance.CheckAllPlayersOutOfEnergy();
//                }

            }
            // Update soul visual transparency
            if (soulSprite != null)
            {
                float alpha = Mathf.Clamp01(energyLevel / capacity);
                Color currentColor = soulSprite.color;
                currentColor.a = alpha;
                soulSprite.color = currentColor;
            }

            // Update UI
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }


        }
    public void CollectEnergy(int amount)
        {
            energyLevel += amount;
            if (energyLevel > capacity)
            {
                energyLevel = capacity;
            }

            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }

            playerStats.RecordItemCollected();
            
        }
    private void UpdateDistanceCovered()
    {
        // Calculate the distance covered since the last frame
        float distance = Vector3.Distance(transform.position, lastPosition);
        playerStats.distanceCovered += (int)distance;
        playerStats.AddScore((int)distance); // Award points for distance

        lastPosition = transform.position;
    }

    private void ClampAngularVelocity()
        {
            float maxAngularVelocity = 540f;
            if (rb != null)
            {
                rb.angularVelocity = Mathf.Clamp(rb.angularVelocity, -maxAngularVelocity, maxAngularVelocity);
            }
        }
    
    private void RefreshVehicleKeepClearPocket()
    { 
        if (!keepDustClearAroundVehicle) return;
        if (drumTrack == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        int ownerId = GetInstanceID();

        if (!boosting)
        {
            gfm.dustGenerator.ReleaseVehicleKeepClear(ownerId, phaseNow);
            return;
        }

        if (Time.time < _nextVehicleKeepClearRefreshAt) return;
        _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleKeepClearRefreshSeconds);

        Vector2 vehicleWorld = rb != null ? rb.position : (Vector2)transform.position;
        Vector2Int cell = drumTrack.WorldToGridPosition(vehicleWorld);
        gfm.dustGenerator.SetVehicleKeepClear(
            ownerId,
            cell,
            vehicleKeepClearRadiusCells,
            phaseNow,
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: 0.20f
        );
    }
    void FixedUpdate() {

        if (incapacitated) return;
    // --- PhaseStar Safety Bubble: dust has no effect inside the bubble ---
        if (PhaseStar.IsPointInsideSafetyBubble(transform.position))
        {
        }

        float dt = Time.fixedDeltaTime;
        RefreshVehicleKeepClearIfNeeded();
        // --- Loop boundary check (null-safe) ---
        if (drumTrack != null)
        {
            float  loopLen = drumTrack.GetLoopLengthInSeconds();
            double dspNow  = AudioSettings.dspTime;
            if (dspNow - loopStartDSPTime >= loopLen)
            {
                loopStartDSPTime = dspNow;
            }
        }

        // --- Input hygiene: if Move() hasn't been called recently, treat as zero ---
        if (Time.time - _lastMoveStamp > inputTimeout) _moveInput = Vector2.zero;

        bool hasInput  = _moveInput.sqrMagnitude > 0.0001f;
        bool canThrust = !requireBoostForThrust || boosting;

        // ---- movement ----
        if(canThrust && (hasInput || boosting)) {
            // Target velocity from input (single env scalar)
            Vector2 steerDir = hasInput ? _moveInput.normalized : (_lastNonZeroInput.sqrMagnitude > 0f ? _lastNonZeroInput : (Vector2)transform.up);
            // Target velocity from steer direction (sing env scalar)
            Vector2 desiredVel = steerDir * arcadeMaxSpeed;
            // Acceleration pick (handles boost-only ships with accel=0)
            float accelUsed;
            if (requireBoostForThrust)
                accelUsed = boosting ? arcadeBoostAccel : 0f;
            else
                accelUsed = (boosting ? arcadeBoostAccel : arcadeAccel);

            if (accelUsed > 0f)
            {
                Vector2 dv      = desiredVel - rb.linearVelocity;
                float   maxStep = accelUsed * dt;
                Vector2 step    = (dv.sqrMagnitude > maxStep * maxStep) ? dv.normalized * maxStep : dv;
                rb.linearVelocity += step;
            }
            
        }
        else
        {
            // MASS-DEPENDENT COAST/BRAKE when no input OR cannot thrust
            Vector2 v = rb.linearVelocity;

            // Viscous brake: F = -k * v  → a = -(k/m) v (heavier ships coast longer)
            if (v.sqrMagnitude > 0f && coastBrakeForce > 0f)
                rb.AddForce(-v * coastBrakeForce, ForceMode2D.Force);

            // Snap to full rest near zero to kill jitter tails
            if (v.magnitude < stopSpeed && Mathf.Abs(rb.angularVelocity) < stopAngularSpeed)
            {
                rb.linearVelocity        = Vector2.zero;
                rb.angularVelocity = 0f;
            }
        }

        // Fuel burn only while boosting (keeps your baseBurnAmount/burnRateMultiplier economy)
        if (boosting && energyLevel > 0f)
        {
            float burn = _burnRateMultiplier * _baseBurnAmount * dt * difficultyMultiplier;
            ConsumeEnergy(burn);
        }

        // Face travel direction
        if (rb.linearVelocity.sqrMagnitude > 0.0001f)
        {
            float angleDeg = Mathf.Atan2(rb.linearVelocity.y, rb.linearVelocity.x) * Mathf.Rad2Deg - 90f;
            rb.rotation = angleDeg;
        }

// Stats + audio

    UpdateDistanceCovered();
    ClampAngularVelocity();
    audioManager.AdjustPitch(rb.linearVelocity.magnitude * 0.1f);
}
    public float ConsumeBoostEnergySpentSinceLastSample()
    {
        float v = _boostEnergySpentAccum;
        _boostEnergySpentAccum = 0f;
        return v;
    }
    public void DrainEnergy(float amount, string source = "Unknown")
{
    if (amount <= 0f) return;

    // Calls your existing clamp/UI logic
    // Count only boost-related drain (or broaden if you want "effort" = all movement).
    if (source == "Boost")
        _boostEnergySpentAccum += amount;

    ConsumeEnergy(amount);
}


    void OnCollisionEnter2D(Collision2D coll)
    {
        Debug.Log($"[VEHICLE:COLLISION] hit '{coll.collider.name}' layer={coll.collider.gameObject.layer}", this);
        var node = coll.gameObject.GetComponent<MineNode>();

        // 🎯 Apply impact damage
        int damage = GetForceAsDamage();
        if (node != null)
        {
            TriggerFlickerAndPulse(1.2f, node.coreSprite.color, false);
            // 💥 Apply knockback
            Rigidbody2D nodeRb = node.GetComponent<Rigidbody2D>();
            if (nodeRb != null)
            {
                Vector2 forceDirection = rb.linearVelocity.normalized;
                float knockbackForce = rb.mass * rb.linearVelocity.magnitude * 0.5f; // Tunable
                nodeRb.AddForce(forceDirection * knockbackForce, ForceMode2D.Impulse);
            }
        }

        if (coll.gameObject.tag == "Bump")
        {
            TriggerThud(coll.contacts[0].point);
        }

        // ---- Boost carving (dust maze) ----
        if (!boosting) return;
        if (gfm == null || gfm.dustGenerator == null) return;

        if (!gfm.dustGenerator.IsDustTerrainCollider(coll.collider)) return;

        // Contact point
        Vector2 contact = (coll.contactCount > 0) ? coll.GetContact(0).point : (Vector2)transform.position;
        Debug.Log($"[DUST] Do Boost Carve at {contact}");
        DoBoostCarve(coll);


// Forward carving budget based on speed (your existing rule)
        float speed = rb != null ? rb.linearVelocity.magnitude : 0f;
        const float lowSpeed = 2.0f;
        const float highSpeed = 7.0f;

        int budget;
        if (speed <= lowSpeed) budget = 1;
        else if (speed >= highSpeed) budget = 10;
        else budget = 2 + Mathf.FloorToInt((speed - lowSpeed) / (highSpeed - lowSpeed) * 2f); // 2..3

        Vector2 v = rb != null ? rb.linearVelocity : Vector2.zero;
        Debug.Log($"[VEHICLE:COLLISION] velocity square magnitude: {v.sqrMagnitude}");
        if (v.sqrMagnitude < 0.0001f) return;

        float cellWorld = Mathf.Max(0.01f, drumTrack.GetCellWorldSize());

        switch (dustCarveMode)
        {
            case DustCarveMode.NearestResolveDisk:
// Carve additional “ahead” bites in world space (more robust with composite collision)
                for (int i = 1; i < budget; i++)
                {
                    Vector2 probe = contact + v.normalized * (cellWorld * 0.95f * i);
                    gfm.dustGenerator.TryCarveDustAtWorldPoint(probe, resolveRadiusCells: 1, fadeSeconds: 0.20f);
                    Debug.Log($"[DUST] Biting additional {i} of {budget}");
                }
                break;

            case DustCarveMode.RayMarchLine:
                Debug.Log($"[VEHICLE:COLLISION] raymarch line budget={budget}");
                Debug.Log($"[DUST] Ray line march to {cellWorld} with budget {budget}");

                CarveForward_RayMarch(contact, v, budget, cellWorld);
                break;

            case DustCarveMode.Wedge:
                CarveForward_Wedge(contact, v, budget, cellWorld);
                break;
        }
        if (boosting && gfm != null && gfm.dustGenerator != null && gfm.dustGenerator.IsDustTerrainCollider(coll.collider))
        {
            _lastDustCollider = coll.collider;
                    }

    }
    void OnCollisionStay2D(Collision2D coll)
    {
        if (!boosting) return;
        if (gfm == null || gfm.dustGenerator == null) return;
        if (!gfm.dustGenerator.IsDustTerrainCollider(coll.collider)) return;

        _lastDustCollider = coll.collider;

        // Rate-limit carving so it’s stable and doesn’t explode CPU
        if (Time.time < _nextCarveTime) return;
        _nextCarveTime = Time.time + (1f / Mathf.Max(1f, carveTickHz));
        Debug.Log($"[DUST] Vehicle continuing boost carving at {coll}");

        DoBoostCarve(coll);
    }
    void OnCollisionExit2D(Collision2D coll)
    {
        if (coll.collider == _lastDustCollider)
        {
            _lastDustCollider = null;
        }
    }
    private void DoBoostCarve(Collision2D collision)
    {
        if (collision.contactCount == 0) return;

        Vector2 contactPoint = collision.GetContact(0).point;

        if (drumTrack == null) return;

        // Vehicle carve: 1 cell wide, temporary, normal regrow
        drumTrack.CarveTemporaryCellFromVehicle(
            contactPoint,
            healDelaySeconds: 4,5   // tune as desired
        );
    }


    private void CarveForward_RayMarch(Vector2 contact, Vector2 vel, int budget, float cellWorld)
    {
        if (vel.sqrMagnitude < 0.0001f) return;

        var gen = gfm.dustGenerator;
        var dt  = drumTrack; // or gfm.activeDrumTrack if you prefer

        Vector2 dir = vel.normalized;

        // March increment: smaller than a cell reduces aliasing and keeps it straight.
        float step = cellWorld * 0.75f;

        // Track last cell to avoid re-carving same cell on adjacent samples.
        Vector2Int lastCell = new Vector2Int(int.MinValue, int.MinValue);

        for (int i = 1; i <= budget; i++)
        {
            Vector2 p = contact + dir * (step * i);
            Vector2Int cell = dt.WorldToGridPosition(p);

            if (cell == lastCell) continue;
            lastCell = cell;

            // IMPORTANT: no neighbor resolution here.
            if (gen.TryGetDustAt(cell, out _))
                gen.CarveDustAt(cell, 0.20f);
        }
    }
    private void CarveForward_Wedge(Vector2 contact, Vector2 vel, int budget, float cellWorld)
    {
        if (vel.sqrMagnitude < 0.0001f) return;

        var gen = gfm.dustGenerator;
        var dt  = drumTrack;

        Vector2 dir  = vel.normalized;
        Vector2 side = new Vector2(-dir.y, dir.x);

        float step = cellWorld * 0.75f;
        float half = cellWorld * 0.45f; // small tube radius

        Vector2Int lastC = new Vector2Int(int.MinValue, int.MinValue);
        Vector2Int lastL = lastC;
        Vector2Int lastR = lastC;

        for (int i = 1; i <= budget; i++)
        {
            Vector2 pC = contact + dir * (step * i);
            Vector2 pL = pC + side * half;
            Vector2 pR = pC - side * half;

            CarveCellOnce(dt.WorldToGridPosition(pC), ref lastC);
            CarveCellOnce(dt.WorldToGridPosition(pL), ref lastL);
            CarveCellOnce(dt.WorldToGridPosition(pR), ref lastR);
        }

        void CarveCellOnce(Vector2Int cell, ref Vector2Int last)
        {
            if (cell == last) return;
            last = cell;
            if (gen.TryGetDustAt(cell, out _))
                gen.CarveDustAt(cell, 0.20f);
        }
    }

    private void CarveIfBeatsHardness(Vector2Int gp, float boostDamage01)
    {
        Debug.Log($"[VEHICLE:DUST] Contact {gp} boost01={boostDamage01}", this);

        if (gfm == null || gfm.dustGenerator == null)
        {
            Debug.LogError("[VEHICLE:DUST] gfm/dustGenerator null", this);
            return;
        }

        if (!gfm.dustGenerator.TryGetDustAt(gp, out var dust) || dust == null)
        {
            Debug.Log($"[VEHICLE:DUST] No dust at {gp}", this);
            return;
        }

        float required = Mathf.Lerp(0.25f, 0.75f, dust.clearing.hardness01);
        required = Mathf.Min(required, boostCarveRequiredFloor01);

        float effectiveBoost = Mathf.Max(boostDamage01, boostCarveDamageFloor01);

        Debug.Log($"[VEHICLE:DUST] effectiveBoost={effectiveBoost:F3} required={required:F3} hardness={dust.clearing.hardness01:F3}", this);

        if (effectiveBoost < required) return;

        Debug.Log($"[VEHICLE:DUST] Carving {gp}", this);
        gfm.dustGenerator.CarveDustAt(gp, fadeSeconds: 0.20f);
    }

    private void TriggerThud(Vector2 collisionPoint)
        {
            if (baseSprite == null || isFlickering) return;

            if (flickerPulseRoutine != null)
            {
                StopCoroutine(flickerPulseRoutine); // Prevent stacking
            }
            flickerPulseRoutine = StartCoroutine(ThudRoutine(collisionPoint));
        }
    private void TriggerFlickerAndPulse(float scaleMultiplier, Color? baseColor = null, bool cycleHue = false)
        {
            if (baseSprite == null || isFlickering) return;

            if (flickerPulseRoutine != null)
            {
                StopCoroutine(flickerPulseRoutine); // Prevent stacking
            }

            flickerPulseRoutine = StartCoroutine(FlickerAndPulseRoutine(scaleMultiplier, baseColor, cycleHue));
        }
    private IEnumerator ThudRoutine(Vector2 coll)
        {
            isFlickering = true;
            yield return VisualFeedbackUtility.BoundaryThudFeedback(baseSprite, transform, coll);
            isFlickering = false;
            flickerPulseRoutine = null;
        }
    private IEnumerator FlickerAndPulseRoutine(float scaleMultiplier, Color? baseColor, bool cycleHue)
        {
            isFlickering = true;

            yield return VisualFeedbackUtility.SpectrumFlickerWithPulse(
                baseSprite,
                transform,
                0.2f,
                scaleMultiplier,
                cycleHue ? null : baseColor,
                cycleHue
            );

            isFlickering = false;
            flickerPulseRoutine = null;
        }

}
