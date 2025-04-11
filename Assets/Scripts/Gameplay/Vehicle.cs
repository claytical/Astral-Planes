using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public static class ShipMusicalProfiles
{
    public static readonly Dictionary<string, ShipMusicalProfile> PresetsByShip = new()
    {
        {
            "Drifter",
            new ShipMusicalProfile {
                shipName = "Drifter",
                allowedMidiPresets = new List<int> { 32, 33, 34, 48, 49, 50 },
                description = "Ambient and steady. Prefers low-end and smooth textures."
            }
        },
        {
            "Flo-Tilla",
            new ShipMusicalProfile {
                shipName = "Flo-Tilla",
                allowedMidiPresets = new List<int> { 0, 1, 3, 88, 89, 90, 115, 116 },
                description = "Swirling and collaborative. Prefers pads, ambient keys, and light rhythmic FX."
            }
        },
        {
            "HeartDrive",
            new ShipMusicalProfile {
                shipName = "HeartDrive",
                allowedMidiPresets = new List<int> { 73, 74, 75, 80, 81, 82, 0, 2 },
                description = "Expressive and melodic. Leans into solos and emotional tones."
            }
        },
        {
            "Howler",
            new ShipMusicalProfile {
                shipName = "Howler",
                allowedMidiPresets = new List<int> { 24, 26, 84, 85, 115, 117 },
                description = "Aggressive and bold. Favors wild leads and gritty percussion."
            }
        },
        {
            "Sandwave",
            new ShipMusicalProfile {
                shipName = "Sandwave",
                allowedMidiPresets = new List<int> { 12, 13, 24, 32, 35 },
                description = "Earthy and grounded. Driven by groove and plucked rhythm."
            }
        },
        {
            "Scout",
            new ShipMusicalProfile {
                shipName = "Scout",
                allowedMidiPresets = new List<int> { 73, 80, 81, 82 },
                description = "Nimble and quick. Loves flutes and light synth melodies."
            }
        },
        {
            "Scuttle",
            new ShipMusicalProfile {
                shipName = "Scuttle",
                allowedMidiPresets = new List<int> { 13, 14, 83, 84, 115, 116 },
                description = "Chaotic fun. Drops scattershot rhythms and sharp accents."
            }
        },
        {
            "X-agong",
            new ShipMusicalProfile {
                shipName = "X-agong",
                allowedMidiPresets = new List<int> { 33, 34, 115, 117, 118 },
                description = "Alien and forceful. Pulses with deep bass and strange percussive tones."
            }
        }
    };
}
public class Vehicle : MonoBehaviour
{

//    public int currentHP;
//    public int maxHP;
    public float capacity = 10f;
    //public float energyCollected;
    public float energyLevel;

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

    public int GetForceAsDamage()
    {
        
        float speed = rb.linearVelocity.sqrMagnitude;
        float normalizedSpeed = Mathf.InverseLerp(0, terminalVelocity, speed);
        return (int)Mathf.Lerp(0f, 100f, normalizedSpeed);
    }

    public float GetForceAsMidiVelocity()
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
            GamepadManager.Instance.CheckAllPlayersOutOfEnergy();
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
    private IEnumerator DelayedInput(float delay)
    {
        isControlDistorted = true;
        yield return new WaitForSeconds(delay);
        isControlDistorted = false;
    }

    void OnCollisionEnter2D(Collision2D coll)
    {

        if (audioManager != null)
        {
            MineNode node = coll.gameObject.GetComponent<MineNode>();
            if (node != null)
            {
                audioManager.PlaySound(collisionClip);
                Rigidbody2D rb2 = node.GetComponent<Rigidbody2D>();
                if (rb2 != null)
                {
                    rb2.bodyType = RigidbodyType2D.Dynamic;
                    rb2.gravityScale = Random.Range(-.02f, .02f);
                }
            }            
        }
        GlitchController.Instance.TriggerHitGlitch();

    }
    public void Explode()
    {
        if(GetComponent<Explode>())
        {
            GetComponent<Explode>().Permanent();
        }
    }
}
