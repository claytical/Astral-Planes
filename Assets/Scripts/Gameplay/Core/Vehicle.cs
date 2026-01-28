using UnityEngine;
using System.Collections;
public class Vehicle : MonoBehaviour
{
    [Header("Impact Dig Tuning")]
    [Tooltip("Maximum trench length (cells) for the best ship at top speed when digging the softest dust.")]
    [SerializeField] private int maxDigCellsSoft = 10;
    [SerializeField] private float minDigCellsWhenBoosting = 1.0f; // allows digging out when boxed in
    [Tooltip("Minimum time between impact digs per vehicle (seconds). Prevents multiple dust colliders from triggering multiple chain reactions on the same strike).")]
    [SerializeField] private float impactDigCooldownSeconds = 0.12f;
    [Tooltip("Per-cell budget cost before hardness. 1.0 means budget is in 'soft cells'.")]
    [SerializeField] private float digBaseCellCost = 1.0f;
    [Tooltip("Additional per-cell cost added by dust hardness01. 1.0 -> default hardness (0.5) costs 1.5 budget per cell.")]
    [SerializeField] private float digHardnessCost = 1.0f;
    private float _lastImpactDigAt = -999f;
    public ShipMusicalProfile profile;
    public float capacity = 10f;
    private float _baseBurnAmount = 1f;
    private float _burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure
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
    private float _cumulativeEnergySpent = 0f;
    [Header("Input Filtering")]
    [SerializeField] float inputDeadzone = 0.20f;   // tune to your stick
    [SerializeField] float inputTimeout  = 0.15f;   // seconds before we auto-zero if Move() isn’t called
   
    private GameFlowManager gfm;
    float   _lastMoveStamp;
    private Vector2 _moveInput;
    public float energyLevel;
    public bool boosting = false;
    private Vector2 _lastNonZeroInput; // remembers the last aim direction
    public GameObject trail; // Trail prefab
    public AudioClip thrustClip;
    public PlayerStats playerStatsUI; // Reference to the PlayerStats UI element
    public PlayerStatsTracking playerStats;
    
    private GameObject activeTrail; // Reference to the currently active trail instance 
    private Rigidbody2D rb;
    private AudioManager audioManager;
    private SpriteRenderer baseSprite;
    [SerializeField] private bool isLocked = false;
    private Vector3 lastPosition;
    private DrumTrack drumTrack;
    private bool incapacitated = false;
    private double loopStartDSPTime;
    private float _lastDamageTime = -1f;
    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;
    
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
            float burn = _burnRateMultiplier * _baseBurnAmount * dt;
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
    public void DrainEnergy(float amount, string source = "Unknown")
{
    if (amount <= 0f) return;

    ConsumeEnergy(amount);
}
    
    public void ApplyShipProfile(ShipMusicalProfile p, bool refillEnergy = true)
    {
        profile = p;
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
    public void SetColor(Color newColor)
    {
        if (baseSprite != null)
            baseSprite.color = newColor;

    }
    public void SyncEnergyUI()
        {
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
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
    private void RefreshVehicleKeepClearIfNeeded() {
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;
        if (!keepDustClearAroundVehicle) return;

        // Throttle refresh
        if (Time.time < _nextVehicleKeepClearRefreshAt) return;
        _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleKeepClearRefreshSeconds);
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

        if (gfm == null) yield break;

        var gen = gfm.dustGenerator;

        if (gen == null || drumTrack == null) yield break;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        // Compute which cell we're currently in.
        Vector2 pos = (rb != null) ? rb.position : (Vector2)transform.position;
        Vector2Int centerCell = drumTrack.WorldToGridPosition(pos);

        // Choose a radius that guarantees we are not born overlapping walls.
        int radiusCells = Mathf.Max(0, spawnRestPocketRadiusCells);
        if (spawnRestPocketAutoRadius)
        {
            float cellWorld = Mathf.Max(0.01f, drumTrack.GetCellWorldSize());
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
    void OnDisable()
    {
        if (gfm == null || gfm.dustGenerator == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        gfm.dustGenerator.ReleaseVehicleKeepClear(GetInstanceID(), phaseNow);
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

            // Update UI
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }
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

        // ---- Impact dig (dust maze) ----
        // Design intent: a single strike on collision entry triggers a chain reaction trench down a grid line.
        if (!boosting) return;
        if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return;

        // Cooldown prevents multiple dust colliders from generating multiple chain reactions on one strike.
        if (Time.time - _lastImpactDigAt < Mathf.Max(0.01f, impactDigCooldownSeconds))
            return;

        _lastImpactDigAt = Time.time;

        DoImpactDig(coll);
}

    // ------------------------------------------------------------------------
    // Impact Dig (chain reaction trench)
    // ------------------------------------------------------------------------
private void DoImpactDig(Collision2D coll)
{
    if (coll == null) return;
    if (coll.contactCount <= 0) return;
    if (!boosting) return;
    if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return;

    var gen = gfm.dustGenerator;

    // --- Choose the contact that best represents "into-surface" impact ---
    Vector2 relV = coll.relativeVelocity;
    if (rb != null && relV.sqrMagnitude < 0.0001f)
        relV = rb.linearVelocity;

    var best = coll.GetContact(0);
    float bestInto = float.NegativeInfinity;

    for (int i = 0; i < coll.contactCount; i++)
    {
        var c = coll.GetContact(i);
        float into = Vector2.Dot(relV, -c.normal); // >0 means we're moving into the surface
        if (into > bestInto)
        {
            bestInto = into;
            best = c;
        }
    }

    Vector2 contactWorld = best.point;

    // --- Impact measurement (only into-normal speed scales trench) ---
    Vector2 v = (rb != null) ? rb.linearVelocity : relV;
    float intoSpeed = Vector2.Dot(v, -best.normal);
    if (intoSpeed < 0f) intoSpeed = 0f;

    // --- Direction ---
    // If you actually hit into the surface, dig straight into it.
    // Otherwise (boxed-in / grazing), dig in the player's intended direction, but only 1 cell.
    const float kMinIntoForMulti = 0.25f; // not a "tuning number" so much as noise floor; adjust if needed
    bool hasMeaningfulImpact = intoSpeed >= kMinIntoForMulti;

    Vector2 digDirWorld;
    if (hasMeaningfulImpact)
    {
        digDirWorld = -best.normal;
    }
    else if (_moveInput.sqrMagnitude > 0.0001f)
    {
        digDirWorld = _moveInput.normalized;
    }
    else if (_lastNonZeroInput.sqrMagnitude > 0.0001f)
    {
        digDirWorld = _lastNonZeroInput.normalized;
    }
    else
    {
        digDirWorld = -best.normal;
    }

    if (digDirWorld.sqrMagnitude < 0.0001f) return;
    digDirWorld.Normalize();

    // --- Resolve entry cell ---
    if (!TryFindEntryDustCell(contactWorld, resolveRadiusCells: 1, out var gp))
        return;

    // Quantize direction to 8-way grid step.
    Vector2Int step = QuantizeDir8_ToGridStep(digDirWorld);
    if (step == Vector2Int.zero) return;

    // --- Determine intended trench length ---
    // Rule: touching + boost always removes exactly 1 cell.
    int cellsToCarve = 1;

    if (hasMeaningfulImpact)
    {
        // Map intoSpeed -> extra cells.
        // Shape: starts at 1, climbs with impact, clamped.
        // You can change these later without changing the model.
        const float kIntoForMax = 6.0f;     // "full-speed hit" reference
        const int   kMaxCells   = 12;       // absolute cap per strike

        float t = Mathf.InverseLerp(kMinIntoForMulti, kIntoForMax, intoSpeed);
        t = Mathf.Clamp01(t);

        // Slightly convex curve so small impacts don't explode into long trenches.
        t = t * t;

        int extra = Mathf.FloorToInt(t * (kMaxCells - 1));
        cellsToCarve = 1 + Mathf.Clamp(extra, 0, kMaxCells - 1);
    }

    // --- Hardness shortens trenches ---
    // Always carve the first cell. For subsequent cells, hardness may terminate.
    // Impact helps push slightly through hardness (optional but feels good).
    float impactT01 = 0f;
    if (hasMeaningfulImpact)
    {
        const float kIntoForMax = 6.0f;
        impactT01 = Mathf.InverseLerp(kMinIntoForMulti, kIntoForMax, intoSpeed);
        impactT01 = Mathf.Clamp01(impactT01);
    }

    float ContinueChance(float hardness01)
    {
        float h = Mathf.Clamp01(hardness01);
        float baseContinue = 1f - h;          // hard => low continue chance
        float impactBonus  = 0.65f * impactT01 * h; // impact slightly counters hardness only when hard
        return Mathf.Clamp01(baseContinue + impactBonus);
    }

    Vector2Int last = new Vector2Int(int.MinValue, int.MinValue);

    for (int i = 0; i < cellsToCarve; i++)
    {
        if (gp == last) { gp += step; continue; }
        last = gp;

        if (!gen.TryGetDustAt(gp, out var dust) || dust == null)
            break;

        // First cell guaranteed.
        if (i > 0 && hasMeaningfulImpact)
        {
            float p = ContinueChance(dust.clearing.hardness01);
            if (UnityEngine.Random.value > p)
                break;
        }

        gen.CarveDustAt(gp, fadeSeconds: 0.20f);
        gp += step;
    }

    Debug.Log($"[VEHICLE:DIG] intoSpeed={intoSpeed:F2} meaningful={hasMeaningfulImpact} cells={cellsToCarve} step={step}");
}
    private bool TryFindEntryDustCell(Vector2 world, int resolveRadiusCells, out Vector2Int gp)
{
    gp = default;
    if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return false;

    var gen = gfm.dustGenerator;
    var dt = drumTrack;

    Vector2Int baseCell = dt.WorldToGridPosition(world);

    // Fast path: direct hit.
    if (gen.TryGetDustAt(baseCell, out var dust) && dust != null)
    {
        gp = baseCell;
        return true;
    }

    int r = Mathf.Clamp(resolveRadiusCells, 0, 4);
    if (r == 0) return false;

    // Search ring (closest-first-ish). This avoids "neighbor resolution" for the whole line;
    // it's only for finding an occupied entry cell when the contact lands between colliders.
    for (int dy = -r; dy <= r; dy++)
    {
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            Vector2Int c = new Vector2Int(baseCell.x + dx, baseCell.y + dy);
            if (gen.TryGetDustAt(c, out dust) && dust != null)
            {
                gp = c;
                return true;
            }
        }
    }

    return false;
}
    private static readonly Vector2Int[] _dir8 =
    {
    new Vector2Int( 1, 0),
    new Vector2Int( 1, 1),
    new Vector2Int( 0, 1),
    new Vector2Int(-1, 1),
    new Vector2Int(-1, 0),
    new Vector2Int(-1,-1),
    new Vector2Int( 0,-1),
    new Vector2Int( 1,-1),
};
    private Vector2Int QuantizeDir8_ToGridStep(Vector2 worldDir)
{
    // Assumes your dust grid axes align with world X/Y.
    // If your grid is rotated, convert worldDir to grid-local here.
    if (worldDir.sqrMagnitude < 0.0001f) return Vector2Int.zero;
    worldDir.Normalize();

    float best = -999f;
    Vector2Int bestStep = Vector2Int.zero;

    for (int i = 0; i < _dir8.Length; i++)
    {
        Vector2 cand = ((Vector2)_dir8[i]).normalized;
        float d = Vector2.Dot(worldDir, cand);
        if (d > best)
        {
            best = d;
            bestStep = _dir8[i];
        }
    }

    return bestStep;
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
