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
    // Passive energy gain (e.g. lingering near a gravity void) that shouldn't count toward
    // playerStats' "items collected" stat the way an actual pickup via CollectEnergy does.
    public void RecoverEnergy(float amount)
    {
        if (amount <= 0f) return;
        energyLevel = Mathf.Min(capacity, energyLevel + amount);
        if (playerStatsUI != null)
        {
            playerStatsUI.UpdateFuel(energyLevel, capacity);
        }
    }

    public void DrainEnergy(float amount, string source = "Unknown")
{
    if (amount <= 0f) return;
    ConsumeEnergy(amount);
}

    public void ReportCollisionResistance(float resistance01) =>
        _dustResistance01 = Mathf.Max(_dustResistance01, Mathf.Clamp01(resistance01));

    // Average fuel ratio (energyLevel/capacity) across all active local players' vehicles.
    // Defaults to 1 (full fuel -> low pressure) when no vehicles are present, so a boot/
    // transition window doesn't read as a spurious fuel-scarcity spike.
    public static float GetFleetFuelRatio01()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || gfm.localPlayers == null || gfm.localPlayers.Count == 0) return 1f;

        float sum = 0f;
        int counted = 0;
        foreach (var lp in gfm.localPlayers)
        {
            var v = lp?.plane;
            if (v == null || v.capacity <= 0f) continue;
            sum += Mathf.Clamp01(v.energyLevel / v.capacity);
            counted++;
        }
        return counted > 0 ? sum / counted : 1f;
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

    private void ConsumeEnergy(float amount)
        {
            // Competitive match over: gameplay is frozen, so survivors must not drain or die while results show.
            if (GameFlowManager.Instance != null && GameFlowManager.Instance.MatchEnded) return;
            energyLevel -= amount;
            energyLevel = Mathf.Max(0, energyLevel);
            // !_isDead guards against re-entering the death cascade if this vehicle is drained
            // again after already dying (e.g. overlapping bomb blast radii in the same frame) —
            // without it, Explode/DiscardCarriedCollectables/OnVehicleDied/CheckAllPlayersOutOfEnergy
            // would all fire a second time before the delayed Destroy(gameObject, 1) catches up.
            if (energyLevel <= 0 && !_isDead)
            {
                _isDead = true;
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.simulated = false;

                // Stop any in-flight damage flicker so it can't keep running after the sprite
                // is hidden below, and so isFlickering/flickerPulseRoutine end in a clean,
                // terminal state instead of a stale non-null/true left over from the coroutine
                // Unity silently halts when this object is eventually destroyed.
                if (flickerPulseRoutine != null)
                {
                    StopCoroutine(flickerPulseRoutine);
                    flickerPulseRoutine = null;
                }
                isFlickering = false;

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
                playerStatsUI.UpdateFuel(energyLevel, capacity, _dustResistance01);
            }
        }
}
