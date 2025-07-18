﻿using System;
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
    public GameObject ghostVehiclePrefab;
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
    private bool isInConstellation = false;
    private Vector2 storedVelocity;

    [SerializeField] private float minAlpha = 0.2f;
    [SerializeField] private float maxAlpha = 1f;
    [SerializeField] private float maxSpeed = 10f;
    private SpriteRenderer baseSprite;
    [SerializeField] private bool isLocked = false;

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
        public bool IsLockedOrHiding()
        {
            return isLocked || (baseSprite != null && !baseSprite.enabled);
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
        

        public void LockControl()
        {
            isLocked = true;
            storedVelocity = rb.linearVelocity;        // 💾 Save current drift
            rb.linearVelocity = Vector2.zero;
            rb.isKinematic = true;
            GetComponent<Collider2D>().enabled = false;
        }

        public void UnlockControl()
        {
            isLocked = false;
            rb.isKinematic = false;
            GetComponent<Collider2D>().enabled = true;
        }
        public void HideVisuals()
        {
            if (baseSprite != null)
                baseSprite.enabled = false;

            if (soulSprite != null)
                soulSprite.enabled = false;

            if (trail != null)
                trail.SetActive(false);
        }
        public IEnumerator ReactivateVisuals(float duration = 0.4f)
        {
            ShowVisuals();

            float t = 0f;
            Vector3 originalScale = transform.localScale;
            transform.localScale = originalScale * 0.3f;

            Color baseColor = baseSprite.color;
            baseColor.a = 0f;
            baseSprite.color = baseColor;

            while (t < duration)
            {
                float p = t / duration;
                transform.localScale = originalScale * Mathf.Lerp(0.3f, 1f, p);
                baseColor.a = Mathf.Lerp(0f, 1f, p);
                baseSprite.color = baseColor;
                t += Time.deltaTime;
                yield return null;
            }

            transform.localScale = originalScale;
            baseColor.a = 1f;
            baseSprite.color = baseColor;
        }

        public void ShowVisuals()
        {
            if (baseSprite != null)
                baseSprite.enabled = true;

            if (soulSprite != null)
                soulSprite.enabled = true;

            if (trail != null)
                trail.SetActive(true);
        }

        public IEnumerator EnterConstellationDrift(List<Vector3> path)
        {
            if (isInConstellation) yield break;
            isInConstellation = true;

            // Optional: disable collisions or switch layer
            Collider2D col = GetComponent<Collider2D>();
            if (col != null) col.enabled = false;

            rb.linearVelocity = Vector2.zero;
            rb.isKinematic = true;

            float segmentDuration = 0.4f;
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 a = path[i];
                Vector3 b = path[i + 1];

                for (float t = 0; t < segmentDuration; t += Time.deltaTime)
                {
                    float lerpT = t / segmentDuration;
                    transform.position = Vector3.Lerp(a, b, Mathf.SmoothStep(0, 1, lerpT));
                    yield return null;
                }
            }

            rb.isKinematic = false;
            if (col != null) col.enabled = true;
            isInConstellation = false;
        }

private IEnumerator TeleportRoutine()
{
    if (drumTrack == null) yield break;

    Vector3 startPos = transform.position;
    rb.linearVelocity = Vector2.zero;
    rb.isKinematic = true;

    // 🎬 1. Shrink vehicle down
    float shrinkDuration = 0.3f;
    for (float t = 0; t < shrinkDuration; t += Time.deltaTime)
    {
        float scale = Mathf.Lerp(1f, 0f, t / shrinkDuration);
        transform.localScale = originalScale * scale;
        yield return null;
    }
    transform.localScale = Vector3.zero;

    // 🌀 2. Generate spiral path
    Vector3 corePos = drumTrack.transform.position;
    float goldenAngle = 137.5f;
    float radiusStep = drumTrack.GetGridCellSize() * 1.2f;
    int spiralSteps = 16;

    List<Vector3> spiralPath = new List<Vector3>();
    for (int i = 0; i < spiralSteps; i++)
    {
        float angleDeg = i * goldenAngle;
        float angleRad = angleDeg * Mathf.Deg2Rad;
        float radius = i * radiusStep;

        Vector2 offset = new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad)) * radius;
        Vector3 probeWorld = corePos + (Vector3)offset;
        spiralPath.Add(probeWorld);
    }

    // 🎯 3. Choose final position (later in the spiral)
    Vector2Int newCell = new Vector2Int(-1, -1);
    Vector3 newPos = startPos;

    for (int i = spiralSteps - 1; i >= spiralSteps / 2; i--)
    {
        Vector2Int probeGrid = drumTrack.WorldToGridPosition(spiralPath[i]);
        if (drumTrack.IsSpawnCellAvailable(probeGrid.x, probeGrid.y))
        {
            newCell = probeGrid;
            newPos = drumTrack.GridToWorldPosition(newCell);
            break;
        }
    }

    if (newCell.x == -1)
    {
        newCell = drumTrack.GetRandomAvailableCell();
        if (newCell.x == -1)
        {
            Debug.LogWarning("❌ No grid cell available for teleport.");
            yield break;
        }
        newPos = drumTrack.GridToWorldPosition(newCell);
    }

    // ✨ 4. Animate ghost movement through spiral
    float moveDuration = 1.2f;
    int segmentCount = spiralPath.Count - 1;
    for (int i = 0; i < segmentCount; i++)
    {
        Vector3 a = spiralPath[i];
        Vector3 b = spiralPath[i + 1];
        float segmentDuration = moveDuration / segmentCount;

        for (float s = 0; s < segmentDuration; s += Time.deltaTime)
        {
            float lerpT = s / segmentDuration;
            transform.position = Vector3.Lerp(a, b, lerpT);

            // Optional: emit trail particles here
            if (i % 2 == 0)
            {
                GameObject p = Instantiate(trailParticlePrefab, transform.position, Quaternion.identity);
                if (p.TryGetComponent<Rigidbody2D>(out var trailRb))
                {
                    Vector2 dir = (b - a).normalized + UnityEngine.Random.insideUnitSphere * 0.2f;
                    trailRb.linearVelocity = dir * UnityEngine.Random.Range(1f, 3f);
                }
            }

            yield return null;
        }
    }

    // 🌀 5. Spawn particle effect at destination
    GameObject preview = Instantiate(teleportationParticles, newPos, Quaternion.identity);
    transform.position = newPos;

    // ✅ Re-occupy grid
    drumTrack.OccupySpawnGridCell(newCell.x, newCell.y, GridObjectType.Note);
    rb.isKinematic = false;

    // 🌱 6. Regrow vehicle from 0 to full size
    float growDuration = 0.3f;
    for (float t = 0; t < growDuration; t += Time.deltaTime)
    {
        float scale = Mathf.Lerp(0f, 1f, t / growDuration);
        transform.localScale = originalScale * scale;
        yield return null;
    }
    transform.localScale = originalScale;

    Destroy(preview, 1.5f);
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
