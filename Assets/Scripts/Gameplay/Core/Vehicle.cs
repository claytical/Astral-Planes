using UnityEngine;
using System.Collections;
using System.Collections.Generic;


public class Vehicle : MonoBehaviour
{
    public ShipMusicalProfile profile;
    public float capacity = 10f;
    private float _baseBurnAmount = 1f;
    private float _burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure
    private float _dustSfxCooldown = 0f;
    private const float DustSfxInterval = 0.10f; // at most 10 Hz when really grinding dust
    private bool _inDust = false;
    private float _lastDustContactTime = -999f;
    [SerializeField] private float dustExitGraceSeconds = 0.20f;    
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

// Single environment scalar (0..1], replaces envSpeedScale/envAccelScale in movement
    [SerializeField] private float envScale = 1f;
    [Header("Input Filtering")]
    [SerializeField] float inputDeadzone = 0.20f;   // tune to your stick
    [SerializeField] float inputTimeout  = 0.15f;   // seconds before we auto-zero if Move() isn’t called
    private float _lastEnergyDrainLogTime = -999f;

    float   _lastMoveStamp;
    private Vector2 _moveInput;

    public float energyLevel;
    public ParticleSystem remixParticleEffect;
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
    public Color profileColor;
    [SerializeField] private float minAlpha = 0.2f;
    [SerializeField] private float maxAlpha = 1f;
    private SpriteRenderer baseSprite;
    [SerializeField] private bool isLocked = false;
    [SerializeField] private SpriteRenderer shipRenderer;
    private Coroutine rainbowRoutine;
    private bool remixTriggeredThisLoop;
    private Vector3 lastPosition;
    private Vector3 originalScale;
    private DrumTrack drumTrack;
    private bool incapacitated = false;
    private double loopStartDSPTime;
    private float boostTimeThisLoop = 0f;
    [SerializeField] public RemixRingHolder remixRingHolder;
    private float _lastDamageTime = -1f;
    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;
    public HashSet<MusicalRole> collectedRemixRoles = new();
    private Color currentColorBlend = Color.white;
    private VehicleRemixController _remixController;
    [SerializeField] HarmonyDirector harmony;
    [SerializeField] private ShipMusicalProfile shipProfile;
    [Header("Dust Legibility Pocket")]
    [SerializeField] private bool keepDustClearAroundVehicle = true;
    [SerializeField] private int vehicleKeepClearRadiusCells = 1;
    [SerializeField] private float vehicleKeepClearRefreshSeconds = 0.10f;
    private float _nextVehicleKeepClearRefreshAt = 0f;

    [Header("Dust Impact Damage (Non-Boost)")]
    [SerializeField] private float dustImpactDrainPerSecondMax = 8f;
    [SerializeField] private float dustImpactForceVelScale = 0.02f;
    [Range(0f, 1f)]
    [SerializeField] private float dustImpactOppositionWeight = 1f;

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

void FixedUpdate()
{

    if (_inDust && (Time.time - _lastDustContactTime) > dustExitGraceSeconds) { 
        ExitDustField();
    }
    if (incapacitated) return;

    float dt = Time.fixedDeltaTime;

    // --- Boost timer (use fixed dt in FixedUpdate) ---
    if (boosting) boostTimeThisLoop += dt;

    // --- Loop boundary check (null-safe) ---
    var gfm   = GameFlowManager.Instance;
    var track = gfm != null ? gfm.activeDrumTrack : null;
    if (track != null)
    {
        float  loopLen = track.GetLoopLengthInSeconds();
        double dspNow  = AudioSettings.dspTime;
        if (dspNow - loopStartDSPTime >= loopLen)
        {
            loopStartDSPTime = dspNow;
            EvaluateRemixCondition();
        }
    }

    // --- Input hygiene: if Move() hasn't been called recently, treat as zero ---
    if (Time.time - _lastMoveStamp > inputTimeout) _moveInput = Vector2.zero;

    bool hasInput  = _moveInput.sqrMagnitude > 0.0001f;
    bool canThrust = !requireBoostForThrust || boosting;

    // ---- movement ----
    if(canThrust && (hasInput || boosting)) {
        // Target velocity from input (single env scalar)
        //Vector2 desiredVel = _moveInput.normalized * (arcadeMaxSpeed * envScale);
        Vector2 steerDir = hasInput ? _moveInput.normalized : (_lastNonZeroInput.sqrMagnitude > 0f ? _lastNonZeroInput : (Vector2)transform.up);
        // Target velocity from steer direction (sing env scalar)
        Vector2 desiredVel = steerDir * (arcadeMaxSpeed * envScale);
        // Acceleration pick (handles boost-only ships with accel=0)
        float accelUsed;
        if (requireBoostForThrust)
            accelUsed = boosting ? (arcadeBoostAccel * envScale) : 0f;
        else
            accelUsed = (boosting ? arcadeBoostAccel : arcadeAccel) * envScale;

        if (accelUsed > 0f)
        {
            Vector2 dv      = desiredVel - rb.linearVelocity;
            float   maxStep = accelUsed * dt;
            Vector2 step    = (dv.sqrMagnitude > maxStep * maxStep) ? dv.normalized * maxStep : dv;
            rb.linearVelocity += step;
        }

        // Boost-time remix visuals (unchanged behavior)
        if (boosting && _remixController != null && _remixController.HasRemixRoles())
        {
            int stepIdx = drumTrack != null ? drumTrack.currentStep : 0;
            _remixController.FixedUpdateBoosting(dt, stepIdx);
            UpdateRemixParticleEmission();
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

// --- Dust weather force-field (terrain maze) ---
if (gfm != null && gfm.dustGenerator != null)
{
    var phaseNow = (gfm.phaseTransitionManager != null) ? gfm.phaseTransitionManager.currentPhase : MusicalPhase.Establish;
    _dustDebugCooldown -= Time.fixedDeltaTime;
    bool debugPulse = _dustDebugCooldown <= 0f;
    Vector3 vehicleWorld = rb.position;
    if (debugPulse) _dustDebugCooldown = DustDebugInterval;
    if (gfm.dustGenerator.TryGetDustWeatherForce(vehicleWorld, phaseNow,
            out Vector2 dustForce, out float influence01, out float drainPerSecond))
    {
        if (debugPulse)
        {
            Debug.Log($"[DustWeather] pulse, phase={phaseNow} pos={transform.position}", this);
        }
        rb.AddForce(dustForce, ForceMode2D.Force);

        // Treat "near dust" like being in dust, but with a grace exit so it doesn’t flicker.
        _inDust = true;
        if (keepDustClearAroundVehicle && Time.time >= _nextVehicleKeepClearRefreshAt)
        {
            _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleKeepClearRefreshSeconds);
            var drumsRef = gfm.activeDrumTrack;
            if (drumsRef != null)
            {
                Vector2Int cell = drumsRef.WorldToGridPosition(vehicleWorld);
                gfm.dustGenerator.SetVehicleKeepClear(GetInstanceID(), cell, vehicleKeepClearRadiusCells, phaseNow);
            }
        }
        
        _lastDustContactTime = Time.time;

        // Energy drain while lingering in dust (non-boost contact), scaled by how "deep" we are.
        if (!boosting && drainPerSecond > 0f)
            DrainEnergy(drainPerSecond * influence01 * dt, "DustWeather");

        if (!boosting && dustImpactDrainPerSecondMax > 0f && rb.linearVelocity.sqrMagnitude > 0.0001f)
        {
            float fMag = dustForce.magnitude;
            float vMag = rb.linearVelocity.magnitude;

            float opposition01 = 1f;
            if (fMag > 0.0001f && vMag > 0.0001f)
            {
                Vector2 fDir = dustForce / fMag;
                Vector2 vDir = rb.linearVelocity / vMag;
                float oppose = Mathf.Clamp01(-Vector2.Dot(fDir, vDir)); // 1 when opposing
                opposition01 = Mathf.Lerp(1f, oppose, dustImpactOppositionWeight);
            }

            float impact01 = Mathf.Clamp01(fMag * vMag * dustImpactForceVelScale) * opposition01;
            float impactDrain = dustImpactDrainPerSecondMax * impact01 * influence01 * dt;
            if (impactDrain > 0f)
                DrainEnergy(impactDrain, "DustImpact");
        }
        
        // SFX feedback (throttled)
        var ctrl = gfm.controller;
        if (ctrl != null)
        {
            _dustSfxCooldown -= Time.fixedDeltaTime;
            if (_dustSfxCooldown <= 0f)
            {
                bool pushing = boosting && rb.linearVelocity.sqrMagnitude > 0.5f;
                var behavior = pushing ? DustBehaviorType.PushThrough : DustBehaviorType.Repel;
                CollectionSoundManager.Instance?.PlayDustInteraction(ctrl, influence01, behavior);
                _dustSfxCooldown = DustSfxInterval;
            }
        }

        // Boost carving still erodes dust (temporary), preserving your "boost to carve" rule.
        if (boosting)
        {
            float appetite = Mathf.Lerp(0.7f, 1.2f, influence01);
            gfm.dustGenerator.ErodeDustDiskFromVehicle(transform.position, appetite);
        }
    }
}
// Stats + audio

    UpdateDistanceCovered();
    ClampAngularVelocity();
    audioManager.AdjustPitch(rb.linearVelocity.magnitude * 0.1f);
}
public void DrainEnergy(float amount, string source = "Unknown")
{
    if (amount <= 0f) return;

    // Calls your existing clamp/UI logic
    ConsumeEnergy(amount); // ConsumeEnergy is currently private :contentReference[oaicite:8]{index=8}

    // Throttle logs so you actually see them without spamming
    if (Time.time - _lastEnergyDrainLogTime > 0.75f)
    {
        _lastEnergyDrainLogTime = Time.time;
    }
}

    public void EnterDustField(float speedScale, float accelScale)
    {
        float incoming = Mathf.Min(speedScale, accelScale);
        float floor    = shipProfile ? shipProfile.envScaleFloor : 0.60f;
        envScale = Mathf.Max(floor, incoming);
        _inDust = true;
        _lastDustContactTime = Time.time;
    }

    public void ExitDustField()
    {
        envScale = 1f;
        _inDust = false;
    }
    public float GetMaxSpeed()
    {
        return arcadeMaxSpeed;
    }
   
    public void AddRemixRole(InstrumentTrack track, MusicalRole role, MinedObjectSpawnDirective directive)
    {
        if (collectedRemixRoles.Contains(role)) return;

        // Store role
        collectedRemixRoles.Add(role);
        Debug.Log($"Remix Role {role} collected");

        // Get role color from profile
        MusicalRoleProfile profile = MusicalRoleProfileLibrary.GetProfile(role);
        Color roleColor = profile.defaultColor;

        // Let the remix controller handle visuals and logic
        _remixController.AddRemixRole(role, roleColor, directive);

        // Optional: give immediate visual feedback (e.g., pulse, swap sprite color)
        ApplyTrackColorToShip(roleColor);
        UpdateRemixParticles();

        // Activate corresponding ring
        if (remixRingHolder != null)
            remixRingHolder.ActivateRing(role, roleColor);
    }
    void OnDisable()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.dustGenerator != null)
        {
            var phaseNow = (gfm.phaseTransitionManager != null) ? gfm.phaseTransitionManager.currentPhase : MusicalPhase.Establish;
            gfm.dustGenerator.ClearVehicleKeepClear(GetInstanceID(), phaseNow);
        }
    }

    public void SetColor(Color newColor)
    {
        if (baseSprite != null)
            baseSprite.color = newColor;

    }

    private void UpdateRemixParticleEmission()
    {
        if (remixParticleEffect == null) return;
        var emission = remixParticleEffect.emission;
        emission.rateOverTime = boosting ? 50f : 5f;

        if (!remixParticleEffect.isPlaying && _remixController.HasRemixRoles())
        {
            remixParticleEffect.Play();
        }
        else if (!_remixController.HasRemixRoles())
        {
            remixParticleEffect.Stop();
            
        }
    }

    private void UpdateRemixParticles()
    {
        if (remixParticleEffect == null) return;

        // Update particle color
        var main = remixParticleEffect.main;
        main.startColor = currentColorBlend;

        // Update emission rate based on boosting
        var emission = remixParticleEffect.emission;
        emission.rateOverTime = boosting ? 50f : 5f; // tweak these values as needed

        if (!remixParticleEffect.isPlaying)
            remixParticleEffect.Play();
    }

    private void ApplyTrackColorToShip(Color newColor)
    {
        if (currentColorBlend == profileColor)
            currentColorBlend = newColor;
        else
            currentColorBlend = Color.Lerp(currentColorBlend, newColor, 0.5f);

        shipRenderer.color = currentColorBlend;
    }

    void Start()
        {
            rb = GetComponent<Rigidbody2D>();
            baseSprite = GetComponent<SpriteRenderer>();
            profileColor = baseSprite.color;
            shipRenderer = baseSprite;
            audioManager = GetComponent<AudioManager>();
            originalScale = transform.localScale;
            _remixController = GetComponent<VehicleRemixController>();
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
        }
    

    private void EvaluateRemixCondition() {
       if(_remixController.EvaluateRemixCondition()) {
       }
    }
            
    public void SetHarmony(HarmonyDirector h) { harmony = h; }
    

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
        if (energyLevel > 0 && !boosting)
        {
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

    private void ConsumeEnergy(float amount)
        {
            energyLevel -= amount;
            energyLevel = Mathf.Max(0, energyLevel); // Clamp to 0
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
    void OnCollisionEnter2D(Collision2D coll)
        {
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
//                CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Boundary);
                TriggerThud(coll.contacts[0].point);
            }
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
