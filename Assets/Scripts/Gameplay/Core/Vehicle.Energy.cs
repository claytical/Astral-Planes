using UnityEngine;

public partial class Vehicle
{
    public void CollectEnergy(float amount)
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
    if (_boostCostFree) return; // free boost phase: skip all external energy drains
    ConsumeEnergy(amount);
}

    private void ApplyShipProfile(ShipMusicalProfile p, bool refillEnergy = true)
    {
        profile = p;

        arcadeMaxSpeed = p.arcadeMaxSpeed;

        rb.mass             = p.mass;
        rb.linearDamping    = p.arcadeLinearDamping;
        rb.angularDamping   = p.arcadeAngularDamping;

        capacity = p.capacity;
        if (refillEnergy) energyLevel = capacity;

        if (p.vehicleKeepClearRadiusCells > 0)
            vehicleKeepClearRadiusCells = p.vehicleKeepClearRadiusCells;
    }

    public void SyncEnergyUI()
        {
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }

        }

    public float GetCumulativeSpentTanks() {
        if (capacity <= 0f) return 0f;
        return _cumulativeEnergySpent / capacity;
    }

    private void ConsumeEnergy(float amount)
        {
            energyLevel -= amount;
            energyLevel = Mathf.Max(0, energyLevel);
            _cumulativeEnergySpent += Mathf.Max(0f, amount);
            if (energyLevel <= 0)
            {
                _isDead = true;
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.simulated = false;

                // Hide the vehicle immediately so the explosion VFX (a separate, independently
                // timed object) reads as the vehicle being destroyed, not as it surviving and
                // flying off while the explosion happens to its name.
                if (baseSprite != null) baseSprite.enabled = false;
                if (soulSprite != null) soulSprite.enabled = false;
                var ownCollider = GetComponent<Collider2D>();
                if (ownCollider != null) ownCollider.enabled = false;

                DiscardCarriedCollectables();
                    Explode explode = GetComponent<Explode>();
                    if (explode != null)
                    {
                        explode.Permanent();
                    }
                    playerStats?.GetComponent<LocalPlayer>()?.OnVehicleDied();
                    gfm?.CheckAllPlayersOutOfEnergy();
//                }

            }

            // Update UI
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }
        }
}
