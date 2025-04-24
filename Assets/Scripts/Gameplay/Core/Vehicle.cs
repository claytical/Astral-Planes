using UnityEngine;
using System.Collections;
using System.Collections.Generic;


public class Vehicle : MonoBehaviour
{
    public float capacity = 10f;
    public float energyLevel;

    public float force = 10f;
    public float terminalVelocity = 20f;
    public float boostMultiplier = 2f;
    private float lastDamageTime = -1f;

    public float baseBurnRate = 1f; // Base burn rate per vehicle
    public float burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure

    public bool boosting;

    public GameObject trail; // Trail prefab

    private GameObject activeTrail; // Reference to the currently active trail instance
    private Rigidbody2D rb;
    private AudioManager audioManager;

    public AudioClip collisionClip;
    public AudioClip thrustClip;
    
    private bool isControlDistorted;
    private Vector2 driftDirection;

    public PlayerStats playerStatsUI; // Reference to the PlayerStats UI element
    public PlayerStatsTracking playerStats;

    private Vector3 lastPosition;
    public SpriteRenderer soulSprite;
    private DrumTrack drumTrack;



    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        audioManager = GetComponent<AudioManager>();

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


    void FixedUpdate()
    {
        float fuelConsumption = 0f;
        float currentMaxSpeed = terminalVelocity;
        float appliedForce = 0f;

        // Apply thrust if boosting and we have energy
        if (boosting && energyLevel > 0)
        {
            appliedForce = force * burnRateMultiplier * boostMultiplier;
            currentMaxSpeed = terminalVelocity * 1.5f;

            fuelConsumption = burnRateMultiplier * baseBurnRate * Time.fixedDeltaTime;
            ConsumeEnergy(fuelConsumption);
            playerStats.RecordFuelUsed(fuelConsumption);
        }
        else if (energyLevel > 0)
        {
            fuelConsumption = .001f;
            ConsumeEnergy(fuelConsumption);
            playerStats.RecordFuelUsed(fuelConsumption);
            
        }

        // Apply force if moving or boosting
        if (appliedForce > 0f)
        {
            rb.AddForce(transform.up * appliedForce, ForceMode2D.Force);
        }

        // Clamp speed to current max (depends on boost)
        if (rb.linearVelocity.magnitude > currentMaxSpeed)
        {
            rb.linearVelocity = rb.linearVelocity.normalized * currentMaxSpeed;
        }

        UpdateDistanceCovered(); // Keep this for score tracking
        ClampAngularVelocity();

        // Optional: Sound pitch scales with speed
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
        return Mathf.Lerp(60f, 127f, normalizedSpeed);
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

    public void Fly()
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
    private void ConsumeEnergy(float amount)
    {
        energyLevel -= amount;
        energyLevel = Mathf.Max(0, energyLevel); // Clamp to 0

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

        if (energyLevel == 0)
        {
            GameFlowManager.Instance.CheckAllPlayersOutOfEnergy();
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
    }

}
