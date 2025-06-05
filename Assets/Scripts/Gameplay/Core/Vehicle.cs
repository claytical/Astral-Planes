using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Random = UnityEngine.Random;


public class Vehicle : MonoBehaviour
{
    public float capacity = 10f;
    public float energyLevel;
    public GameObject teleportationParticles;
    public GameObject trailParticlePrefab;

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
    public AudioClip collisionClip;
    public AudioClip thrustClip;
    
    public PlayerStats playerStatsUI; // Reference to the PlayerStats UI element
    public PlayerStatsTracking playerStats;
    public SpriteRenderer soulSprite;

    private GameObject activeTrail; // Reference to the currently active trail instance 
    private Rigidbody2D rb;
    private AudioManager audioManager;

    [SerializeField] private float minAlpha = 0.2f;
    [SerializeField] private float maxAlpha = 1f;
    [SerializeField] private float maxSpeed = 10f;
    private SpriteRenderer baseSprite;

    private Vector3 lastPosition;
    private Vector3 originalScale;
    private DrumTrack drumTrack;
    private bool incapacitated = false;

    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;

        void Start()
        {
    //        baseBurnAmount = capacity / 90f; // e.g., 1.1–1.3
            rb = GetComponent<Rigidbody2D>();
            baseSprite = GetComponent<SpriteRenderer>();
            audioManager = GetComponent<AudioManager>();
            originalScale = transform.localScale;
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

        private void Update()
        {
            UpdateVisualAlpha();
        }
        private void UpdateVisualAlpha()
        {
            if (baseSprite == null) return;

            float currentSpeed = rb.linearVelocity.magnitude;
            float speedRatio = Mathf.Clamp01(currentSpeed / maxSpeed);
            float alpha = Mathf.Lerp(minAlpha, maxAlpha, speedRatio);

            Color currentColor = baseSprite.color;
            currentColor.a = alpha;
            baseSprite.color = currentColor;
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
        
        public void TeleportToRandomCell()
        {
            StartCoroutine(TeleportRoutine());
        }

        private IEnumerator TeleportRoutine()
        {
         
            Vector3 startPos = transform.position;
            Vector2 entryVelocity = rb.linearVelocity.normalized; // use current direction
            Vector2Int entryGrid = drumTrack.WorldToGridPosition(startPos);

            Vector2Int newCell = new Vector2Int(-1, -1); // default to invalid
            int maxSteps = 8;

            for (int i = 1; i <= maxSteps; i++)
            {
                Vector2 offset = entryVelocity * i * drumTrack.GetGridCellSize();
                Vector3 probeWorld = startPos + (Vector3)offset;
                Vector2Int probeGrid = drumTrack.WorldToGridPosition(probeWorld);

                if (drumTrack.IsSpawnCellAvailable(probeGrid.x, probeGrid.y))
                {
                    newCell = probeGrid;
                    break;
                }
            }

    // ⛳ Fallback if directional path is blocked
            if (newCell.x == -1)
            {
                newCell = drumTrack.GetRandomAvailableCell();
                if (newCell.x == -1)
                {
                    Debug.LogWarning("❌ No grid cell available for teleport.");
                    yield break;
                }
            }

            float duration = 0.3f;

            // 🌀 Shrink to 0
            for (float t = 0; t < duration; t += Time.deltaTime)
            {
                float scale = Mathf.Lerp(1f, 0f, t / duration);
                transform.localScale = originalScale * scale;
                yield return null;
            }
            transform.localScale = Vector3.zero;
            // ⛳ Find new grid cell
            
            if (drumTrack == null) yield break;

             newCell = drumTrack.GetRandomAvailableCell();
            if (newCell.x == -1)
            {
                Debug.LogWarning("❌ No grid cell available for teleport.");
                yield break;
            }

            Vector3 newPos = drumTrack.GridToWorldPosition(newCell);
            GameObject preview = Instantiate(teleportationParticles, newPos, Quaternion.identity);
            StartCoroutine(EmitTrail(startPos, newPos, 0.4f, 16));
            rb.linearVelocity = Vector3.zero;
            yield return new WaitForSeconds(0.6f); // ⏳ Let the preview bubble up
            transform.position = newPos;

            // ✅ Re-occupy the grid cell
            drumTrack.OccupySpawnGridCell(newCell.x, newCell.y, GridObjectType.Note);

            // 🌱 Grow back to full size
            for (float t = 0; t < duration; t += Time.deltaTime)
            {
                float scale = Mathf.Lerp(0f, 1f, t / duration);
                transform.localScale = originalScale * scale;
                yield return null;
            }

            transform.localScale = originalScale;
            Destroy(preview, 1.5f); // Let it fade out naturally

        }

        void FixedUpdate()
        {
            if (incapacitated) return;

            float fuelConsumption = 0f;
            float currentMaxSpeed = terminalVelocity;
            float appliedForce = 0f;

            // Boosting movement
            if (boosting && energyLevel > 0)
            {
                appliedForce = force * burnRateMultiplier * boostMultiplier;
                currentMaxSpeed = terminalVelocity * 1.5f;

                fuelConsumption = burnRateMultiplier * baseBurnAmount * Time.fixedDeltaTime;
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

        public int GetForceAsDamage()
        {
            float speed = rb.linearVelocity.magnitude;
            float impactCapVelocity = 32f;
            float normalizedSpeed = Mathf.InverseLerp(0f, impactCapVelocity, speed);
            float curvedSpeed = Mathf.Pow(normalizedSpeed, 1.75f);
            float baseDamage = Mathf.Lerp(0f, 100f, curvedSpeed); 

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
                Vector2 forceDirection = direction.normalized * force;
                rb.AddForce(forceDirection, ForceMode2D.Force);
                ConsumeEnergy(.01f);

            }
        }

        IEnumerator EmitTrail(Vector3 from, Vector3 to, float duration, int count = 12)
        {
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                Vector3 pos = Vector3.Lerp(from, to, t);
                GameObject p = Instantiate(trailParticlePrefab, pos, Quaternion.identity);

                // Optional: add directional spark motion
                if (p.TryGetComponent<Rigidbody2D>(out var rb))
                {
                    Vector2 dir = (to - from).normalized + UnityEngine.Random.insideUnitSphere * 0.2f;
                    rb.linearVelocity = dir * Random.Range(1f, 3f);
                }

                yield return new WaitForSeconds(duration / count);
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

                if (audioManager != null && thrustClip != null)
                {
                    audioManager.PlayLoopingSound(thrustClip, .5f);
                }

                Fly(); // Activate the trail when boosting start
            }

            burnRateMultiplier = Mathf.Max(0.2f, triggerValue); // Ensure a minimum multiplier for adequate thrust
        }

        public void ApplyImpulse(Vector2 force)
        {
            if (TryGetComponent<Rigidbody2D>(out var rb))
            {
                rb.AddForce(force, ForceMode2D.Impulse);
            }
        }
        public void ApplyGravitationalForce(Vector2 force)
        {
            if (TryGetComponent<Rigidbody2D>(out var rb))
            {
                rb.AddForce(force, ForceMode2D.Force);
            }
        }

        public void TurnOffBoost()
        {
            if (audioManager != null)
            {
                audioManager.StopSound();
            }
            boosting = false;
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

                var clearedTrack = drumTrack.trackController.ClearAndReturnTrack(this);
                if (clearedTrack != null)
                {
                    energyLevel = capacity;
                    Debug.Log("Recharged from clearing " + clearedTrack.assignedRole);
                    SyncEnergyUI();
                }
                else
                {
                    Debug.Log($"No track to clear...");
                    Explode explode = GetComponent<Explode>();
                    if (explode != null)
                    {
                        explode.Permanent();
                    }
                    GameFlowManager.Instance.CheckAllPlayersOutOfEnergy();
                }

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
                node.ReceiveDamage(damage); // Assume you create this method

                // 💥 Apply knockback
                Rigidbody2D nodeRb = node.GetComponent<Rigidbody2D>();
                if (nodeRb != null)
                {
                    Vector2 forceDirection = rb.linearVelocity.normalized;
                    float knockbackForce = rb.mass * rb.linearVelocity.magnitude * 0.5f; // Tunable
                    nodeRb.AddForce(forceDirection * knockbackForce, ForceMode2D.Impulse);
                }
            }

            MIDISoundEffect mSfx = coll.gameObject.GetComponent<MIDISoundEffect>();
            if (mSfx != null)
            {
                mSfx.Collide();
            }

            if (coll.gameObject.tag == "Bump")
            {
                TriggerFlickerAndPulse(.5f, null, true);

            }
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
