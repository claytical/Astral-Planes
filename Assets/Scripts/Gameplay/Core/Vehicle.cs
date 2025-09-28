using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Random = UnityEngine.Random;


public class Vehicle : MonoBehaviour
{
    public float capacity = 10f;
    public float energyLevel;
    public ParticleSystem remixParticleEffect;
    public float force = 10f;
    public float terminalVelocity = 20f;
    public float boostMultiplier = 2f;
    private float lastDamageTime = -1f;
    public float friction = 0.05f;
    public float difficultyMultiplier = 1f;
    public float baseBurnAmount = 5f;
    public float burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure

    public bool boosting = false;

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
    [SerializeField] private float maxSpeed = 10f;
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

    public float boostPadding = 0.3f; // configurable padding in seconds

    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;
    public HashSet<MusicalRole> collectedRemixRoles = new();
    private const int maxRemixCapacity = 4;
    private float remixChargeTime = 0f;
    private const float remixBoostDuration = 4f;
    private bool remixCharging = false;
    private Color currentColorBlend = Color.white;
    private VehicleRemixController remixController;
    private float envSpeedScale = 1f;
    private float envAccelScale = 1f;
    private float envExtraDamping = 0f;
    private int   envStacks = 0;
    private int   envInsideCount = 0;   // support overlapping dust
    [SerializeField] HarmonyDirector harmony;
    void FixedUpdate()
        {
            if (incapacitated) return;
            if (boosting)
            {
                boostTimeThisLoop += Time.deltaTime;
            }
            // Check loop boundaries
            float loopLength = drumTrack.GetLoopLengthInSeconds();
            double dspNow = AudioSettings.dspTime;
            if (dspNow - loopStartDSPTime >= loopLength)
            {
                loopStartDSPTime = dspNow;
                EvaluateRemixCondition();  // now calls controller.EvaluateRemixCondition()
            }        
            
            float fuelConsumption = 0f;
            float currentMaxSpeed = terminalVelocity;
            float appliedForce = 0f;
// Always respect environment slow (even when not boosting)
            currentMaxSpeed *= envSpeedScale;

            // Boosting movement
            if (boosting && energyLevel > 0)
            {
                appliedForce = force * burnRateMultiplier * boostMultiplier; // existing【:contentReference[oaicite:10]{index=10}】
                fuelConsumption = burnRateMultiplier * baseBurnAmount * Time.fixedDeltaTime;

                if (remixController.HasRemixRoles())
                {
                    remixController.FixedUpdateBoosting(Time.fixedDeltaTime, drumTrack.currentStep);
                    UpdateRemixParticleEmission();
                }

                currentMaxSpeed *= envSpeedScale;
                appliedForce    *= envAccelScale;
            }
            
            // Apply fuel drain (if any)
            if (fuelConsumption > 0f)
            {
                fuelConsumption *= difficultyMultiplier;
                ConsumeEnergy(fuelConsumption);
                playerStats.RecordFuelUsed(fuelConsumption);
            }

            // Apply thrust force
            if (appliedForce > 0f)
            {
                rb.AddForce(transform.up * appliedForce * friction, ForceMode2D.Force);
            }

            // Clamp speed
            if (rb.linearVelocity.magnitude > currentMaxSpeed)
            {
                rb.linearVelocity = rb.linearVelocity.normalized * currentMaxSpeed;
            }

            // Stat tracking
            UpdateDistanceCovered();
            ClampAngularVelocity();

            // Audio feedback
            audioManager.AdjustPitch(rb.linearVelocity.magnitude * 0.1f);
        }

    public void EnterDustField(float speedScale, float accelScale) {
        envInsideCount++;
        envSpeedScale = Mathf.Min(envSpeedScale, speedScale);
        envAccelScale = Mathf.Min(envAccelScale, accelScale);
    }

    public void ExitDustField() {
        envInsideCount = Mathf.Max(0, envInsideCount - 1);
        if (envInsideCount == 0) {
            envSpeedScale = 1f;
            envAccelScale = 1f;
        }
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
        remixController.AddRemixRole(role, roleColor, directive);

        // Optional: give immediate visual feedback (e.g., pulse, swap sprite color)
        ApplyTrackColorToShip(roleColor);
        UpdateRemixParticles();

        // Activate corresponding ring
        if (remixRingHolder != null)
            remixRingHolder.ActivateRing(role, roleColor);
    }

    public void SetColor(Color newColor)
    {
        if (baseSprite != null)
            baseSprite.color = newColor;

    }

    private void UpdateRemixParticleEmission()
    {
        if (remixParticleEffect == null) return;
        Debug.Log($"UpdateRemixParticleEmission");
        var emission = remixParticleEffect.emission;
        emission.rateOverTime = boosting ? 50f : 5f;

        if (!remixParticleEffect.isPlaying && remixController.HasRemixRoles())
        {
            Debug.Log("Playing Remix Particle Effect");
            remixParticleEffect.Play();
        }
        else if (!remixController.HasRemixRoles())
        {
            Debug.Log("Stopping particles");
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
            remixController = GetComponent<VehicleRemixController>();
            loopStartDSPTime = GameFlowManager.Instance.activeDrumTrack.startDspTime;
            if (rb == null)
            {
                Debug.LogError("❌ Rigidbody2D component is missing.");
                enabled = false;
                return;
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
       if(remixController.EvaluateRemixCondition()) {
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
            if (Time.time - lastDamageTime < 0.5f)
            {
                damage = Mathf.Max(damage, 10f); // Floor for quick follow-ups
            }

            lastDamageTime = Time.time;

            return Mathf.RoundToInt(Mathf.Clamp(damage, 0f, 120f));
        }
    public float GetForceAsMidiVelocity()
        {
            
            float speed = rb.linearVelocity.sqrMagnitude;
            float normalizedSpeed = Mathf.InverseLerp(0, terminalVelocity * terminalVelocity, speed);
            return Mathf.Lerp(80f, 127f, normalizedSpeed);
        }
    public void Move(Vector2 direction)
        {
            if (isLocked) return;
            if (rb != null && direction != Vector2.zero)
            {
                // Reset the existing rotational force (angular velocity)
                rb.angularVelocity = 0f;

                // Calculate the angle of the movement direction
                float angleRad = Mathf.Atan2(direction.y, direction.x);
                float angleDeg = angleRad * Mathf.Rad2Deg;

                // Adjust the angle to account for the sprite's initial orientation (facing up)
                angleDeg -= 90f;

                // Rotate the vehicle to face the movement direction
                rb.rotation = angleDeg;

                // Apply force in the direction of movement
                Vector2 forceDirection = direction.normalized * (force * envAccelScale);
                rb.AddForce(forceDirection, ForceMode2D.Force);
                if (envInsideCount > 0)
                {
                    rb.AddForce(-rb.linearVelocity * (1f - envAccelScale) * 3f, ForceMode2D.Force);
                }

                ConsumeEnergy(.01f);

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

            // seconds remaining in the current drum loop (with a small safety pad)
            float remain = drumTrack.GetTimeToLoopEnd();   // <- use the helper
            harmony?.BeginBoostArp(Mathf.Max(0.05f, remain)); // start arp toward the loop end

            if (audioManager != null && thrustClip != null)
                audioManager.PlayLoopingSound(thrustClip, .5f);

            Fly();
        }

        burnRateMultiplier = Mathf.Max(0.2f, triggerValue);
    }

    public void TurnOffBoost()
    {
        if (audioManager != null)
        {
            audioManager.StopSound();
        }
        boosting = false;
        harmony?.CancelBoostArp();
        remixController.ResetRemixVisuals();
        burnRateMultiplier = 0f; // Reset the multiplier when not boosting

        // Disable the trail's emission when boosting stops
        if (activeTrail != null)
        {
            activeTrail.GetComponent<TrailRenderer>().emitting = false;
        }
    }
    public void ConsumeEnergy(float amount)
        {
            energyLevel -= amount;
            energyLevel = Mathf.Max(0, energyLevel); // Clamp to 0
            if (energyLevel <= 0)
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                Debug.Log($"[DEBUG] energyLevel={energyLevel}, drumTrack={drumTrack}, trackController={drumTrack?.trackController}");

//                var clearedTrack = drumTrack.trackController.ClearAndReturnTrack(this);
  //              if (clearedTrack != null)
  //              {
  //                  energyLevel = capacity;
  //                  Debug.Log("Recharged from clearing " + clearedTrack.assignedRole);
  //                  SyncEnergyUI();
  //              }
  //              else
  //              {
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
                CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Boundary);
                TriggerThud(coll.contacts[0].point);
            }
        }

    public void TriggerThud(Vector2 collisionPoint)
        {
            if (baseSprite == null || isFlickering) return;

            if (flickerPulseRoutine != null)
            {
                StopCoroutine(flickerPulseRoutine); // Prevent stacking
            }
            flickerPulseRoutine = StartCoroutine(ThudRoutine(collisionPoint));
        }
    public void TriggerFlickerAndPulse(float scaleMultiplier, Color? baseColor = null, bool cycleHue = false)
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
