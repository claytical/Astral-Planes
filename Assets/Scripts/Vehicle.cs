using UnityEngine;
using System.Collections;

public class Vehicle : MonoBehaviour
{

    public int currentHP;
    public int maxHP;

    public float force = 10f;
    public float terminalVelocity = 20f;
    public float boostMultiplier = 2f;
    public float minimalThrustForce = 0.2f;

    public float baseBurnRate = 1f; // Base burn rate per vehicle
    public float burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure

    private bool boosting;

    public GameObject trail; // Trail prefab

    private GameObject activeTrail; // Reference to the currently active trail instance
    private Rigidbody2D rb;
    private AudioManager audioManager;

    public AudioClip collisionClip;
    public AudioClip destroyClip;
    public AudioClip collectedClip;
    public AudioClip thrustClip;

    
    private bool isControlDistorted;
    public float capacity = 10f;
    //public float energyCollected;
    public float energyLevel;
    private Vector2 driftDirection;

    public PlayerStats playerStatsUI; // Reference to the PlayerStats UI element
    public PlayerStatsTracking playerStats;

    private Vector3 lastPosition;
    public SpriteRenderer soulSprite;
    private DrumTrack drumTrack;



    void Start()
    {   // Ensure each moodObject has a MidiListPlayer attached
        // Get the child SpriteRenderer (make sure the child has the component)

        rb = GetComponent<Rigidbody2D>();
        if (rb == null)
        {
            Debug.LogError("Rigidbody2D component is missing from the GameObject.");
            enabled = false;
            return;
        }

        audioManager = GetComponent<AudioManager>();
        if (audioManager == null)
        {
            Debug.LogError("AudioManager component is missing from the GameObject.");
            enabled = false;
            return;
        }
        energyLevel = capacity;
        // Ensure playerStatsUI is assigned
        if (playerStatsUI == null)
        {
            Debug.LogError("PlayerStats UI is not assigned. Please assign it in the Inspector.");
            enabled = false;
            return;
        }

        UpdateFuelUI();

        if (playerStats == null)
        {
            Debug.LogError("PlayerStatsTracking component is missing.");
            enabled = false;
            return;
        }

        lastPosition = transform.position; // Initialize last position for distance tracking

        }


    void FixedUpdate()
    {
        if (boosting && energyLevel > 0)
        {
            if (!isControlDistorted)
            {
                float appliedForce = force * burnRateMultiplier * boostMultiplier;
                Vector2 forceDirection = transform.up * appliedForce;
                rb.AddForce(forceDirection, ForceMode2D.Force);

                if (rb.linearVelocity.magnitude > terminalVelocity)
                {
                    rb.linearVelocity = rb.linearVelocity.normalized * terminalVelocity;
                }
                
            }

            // Consume fuel based on the pressure applied to the trigger
            float fuelConsumption = burnRateMultiplier * baseBurnRate * Time.fixedDeltaTime;
            
            ConsumeEnergy(fuelConsumption);
            
            playerStats.RecordFuelUsed(fuelConsumption);
        }

        UpdateDistanceCovered(); // Track distance in FixedUpdate

        ClampAngularVelocity();
        audioManager.AdjustPitch(rb.linearVelocity.magnitude * 0.1f);
    }

    public float GetForce()
    {
        
        float speed = rb.linearVelocity.sqrMagnitude;
        float normalizedSpeed = Mathf.InverseLerp(0, terminalVelocity, speed);
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

    public bool IsFlying()
    {
        return activeTrail != null && activeTrail.GetComponent<TrailRenderer>().emitting; // Returns true if the trail is emitting
    }

    public void TurnOnBoost(float triggerValue)
    {
        if (energyLevel > 0 && !boosting)
        {
            boosting = true;

            if (audioManager != null && thrustClip != null)
            {
                audioManager.PlayLoopingSound(thrustClip);
            }

            Fly(); // Activate the trail when boosting start
        }

        burnRateMultiplier = Mathf.Max(0.2f, triggerValue); // Ensure a minimum multiplier for adequate thrust
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
        if (energyLevel < 0) energyLevel = 0;
        UpdateFuelUI();
    }
    public void CollectEnergy(int amount)
    {
        
        energyLevel += amount;
        if (energyLevel > capacity)
        {
            energyLevel = capacity;
        }

        UpdateFuelUI();
        playerStats.RecordItemCollected();



        var localPlayer = GetComponentInParent<LocalPlayer>();
        if (localPlayer != null)
        {
            localPlayer.EnergyCollected((int)energyLevel);
        }
    }
    
    private void UpdateFuelUI()
    {
        // Map energy level to alpha (clamped between 0 and 1)
        if (soulSprite != null)
        {
            float alpha = Mathf.Clamp01(energyLevel / capacity);
            Color currentColor = soulSprite.color;
            currentColor.a = alpha;
            soulSprite.color = currentColor;
        }
        if (playerStatsUI != null)
        {
            playerStatsUI.UpdateFuel((int)energyLevel); // Use playerStatsUI to update fuel
        }
    }

    private void ClampAngularVelocity()
    {
        float maxAngularVelocity = 540f;
        if (rb != null)
        {
            rb.angularVelocity = Mathf.Clamp(rb.angularVelocity, -maxAngularVelocity, maxAngularVelocity);
        }
    }
    public void ApplyControlDistortion()
    {
        float distortionChance = 0.3f; // ✅ 30% chance per frame for control interference
        float inputDelay = Random.Range(0.05f, 0.2f); // ✅ Small input lag

        if (Random.value < distortionChance)
        {
            StartCoroutine(DelayedInput(inputDelay));
        }
    }

    private IEnumerator DelayedInput(float delay)
    {
        isControlDistorted = true;
        yield return new WaitForSeconds(delay);
        isControlDistorted = false;
    }

    void OnCollisionEnter2D(Collision2D coll)
    {
        var platform = coll.gameObject.GetComponentInParent<Platform>();
        if (platform != null && !platform.indestructable)
        {

            var explode = coll.gameObject.GetComponent<Explode>();
            if (explode != null)
            {
                explode.UntilNextSet();
            }
        }
        if (audioManager != null)
        {
            Obstacle obstacle = coll.gameObject.GetComponent<Obstacle>();
            if (obstacle != null)
            {
                audioManager.PlaySound(collisionClip);
                Rigidbody2D rb2 = obstacle.GetComponent<Rigidbody2D>();
                if (rb2 != null)
                {
                    rb2.bodyType = RigidbodyType2D.Dynamic;
                    rb2.gravityScale = Random.Range(-.01f, .01f);
                }
            }            
            DrumLoopCollectable dlc = coll.gameObject.GetComponent<DrumLoopCollectable>();
            if (dlc != null)
            {
                audioManager.PlaySound(collectedClip);
            }
            Hazard hazard = coll.gameObject.GetComponent<Hazard>();
            if (hazard != null)
            {
                audioManager.PlaySound(destroyClip);
            }

        }

    }
    public void Explode()
    {

        if(GetComponent<Explode>())
        {
            GetComponent<Explode>().Permanent();
        }
    }
}
