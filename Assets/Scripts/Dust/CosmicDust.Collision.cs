using System;
using UnityEngine;

public partial class CosmicDust
{
    [Serializable]
    public struct DustPluckSettings
    {
        [Header("Dust Musical Swell")]
        [Tooltip("How long contact needs to build before plucks reach full intensity.")]
        [Range(0.1f, 4f)] public float swellSeconds;
        [Tooltip("Short/long pluck lengths used at low/high contact intensity.")]
        [Min(1)] public int minDurationTicks;
        [Min(1)] public int maxDurationTicks;
        [Tooltip("Soft/loud pluck velocities used at low/high contact intensity.")]
        [Range(1f, 127f)] public float minVelocity127;
        [Range(1f, 127f)] public float maxVelocity127;
        [Tooltip("Time between plucks. Max applies at first contact, min after sustained pressure.")]
        [Min(0.01f)] public float minCooldownSeconds;
        [Min(0.01f)] public float maxCooldownSeconds;
    }

    [SerializeField] private DustPluckSettings pluck = new DustPluckSettings
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
        float chargeDrain = drainPerSec * Mathf.Lerp(1.0f, 0.33f, drainRes) * dt;
        float taken = DrainCharge(chargeDrain);

        vehicle.DrainEnergy(drainPerSec * Mathf.Lerp(0.1f, 1.0f, drainRes) * dt);
        if (taken > 0.001f) TriggerChargeTintPulse();
        if (_currentEnergyUnits > 0) return;

        var gp = _drumTrack.WorldToGridPosition(transform.position);
        gen.ClearCell(gp, CosmicDustGenerator.DustClearMode.FadeAndHide,
            fadeSeconds: _timings.clearFadeOutSeconds, scheduleRegrow: true, runPreExplode: true);
    }

    private void HandleNonBoostCollision(Vehicle vehicle, float dt)
    {
        _nonBoostClearSeconds += dt;
        if (_currentPluckVehicle != vehicle)
        {
            _currentPluckVehicle = vehicle;
            _nextDustPluckTime = Time.time;
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

        GameFlowManager.Instance?.controller?.PlayDustChordPluck(Role, bloom01, 4, durTicks, vel127);
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
