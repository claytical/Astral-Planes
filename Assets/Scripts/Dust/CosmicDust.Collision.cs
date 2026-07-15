using System;
using UnityEngine;

public partial class CosmicDust
{

    [SerializeField] private DustPluckSettings pluck = new()
    {
        swellSeconds = 1.4f,
        minDurationTicks = 360,
        maxDurationTicks = 1440,
        minVelocity127 = 40f,
        maxVelocity127 = 70f,
        minCooldownSeconds = 0.8f,
        maxCooldownSeconds = 2f
    };

    private void OnCollisionStay2D(Collision2D collision)
    {
        if (_isDespawned || _isBreaking) return;
        if (gen == null || _drumTrack == null) return;

        var vehicle = collision.collider != null ? collision.collider.GetComponent<Vehicle>() : null;
        if (vehicle == null) return;
        DriveVehicleCompression(vehicle, collision);
        float dt = Time.fixedDeltaTime;

        if (vehicle.boosting) HandleBoostCollision(vehicle, dt);
        else HandleNonBoostCollision(vehicle, dt);
    }

    private void HandleBoostCollision(Vehicle vehicle, float dt)
    {
        float drainRes = Mathf.Clamp01(clearing.drainResistance01);
        float drainPerSec = Mathf.Max(0f, interaction.energyDrainPerSecond);

        var gp = _drumTrack.WorldToGridPosition(transform.position);
        float bypass = vehicle.profile != null ? vehicle.profile.carveResistanceBypass01 : 0f;
        float liveResistance = gen.GetLiveCarveResistance01(gp);
        float effectiveResistance = liveResistance * (1f - Mathf.Clamp01(bypass));

        float energyCostMul = Mathf.Lerp(1f, 5f, liveResistance);
        vehicle.DrainEnergy(drainPerSec * Mathf.Lerp(0.1f, 1.0f, drainRes) * energyCostMul * dt);

        if (effectiveResistance >= 1f) return;

        gen.CarveDustByVehicle(gp, _timings.clearFadeOutSeconds, vehicle.profile);
        TriggerChargeTintPulse();
    }

    private void HandleNonBoostCollision(Vehicle vehicle, float dt)
    {
        _nonBoostClearSeconds += dt;
        if (_currentPluckVehicle != vehicle)
        {
            _currentPluckVehicle = vehicle;
            _nextDustPluckTime = Time.time;
            if (_hiddenHintColor.a > 0f)
                TriggerHiddenHintPulse();
            else
                TriggerDenyTintPulse(1);
            TriggerJiggle();
        }

        if (Role == MusicalRole.None || Time.time < _nextDustPluckTime) return;
        PlayNonBoostDustPluck();
    }

    private void PlayNonBoostDustPluck()
    {
        float hold01 = Mathf.Clamp01(_nonBoostClearSeconds / Mathf.Max(0.01f, pluck.swellSeconds));
        float bloom01 = hold01 * hold01;
        int durTicks = Mathf.RoundToInt(Mathf.Lerp(pluck.minDurationTicks, pluck.maxDurationTicks, bloom01));
        float vel127 = Mathf.Lerp(pluck.minVelocity127, pluck.maxVelocity127, bloom01);
        float cooldown = Mathf.Lerp(pluck.maxCooldownSeconds, pluck.minCooldownSeconds, bloom01);

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        _gfm?.controller?.PlayDustChordPluck(Role, bloom01, 4, durTicks, vel127);
        _nextDustPluckTime = Time.time + cooldown;
    }

    private void OnCollisionExit2D(Collision2D collision)
    {
        var vehicle = collision.collider != null ? collision.collider.GetComponent<Vehicle>() : null;
        if (vehicle == null) return;
        _nonBoostClearSeconds = 0f;
        ResetVisualToBase();

        if (_currentPluckVehicle == vehicle)
        {
            _currentPluckVehicle = null;
            _nextDustPluckTime = -999f;
        }
    }
}
