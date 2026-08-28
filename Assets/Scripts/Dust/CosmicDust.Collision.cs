using System;
using System.Collections;
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
        minCooldownSeconds = 3f,
        maxCooldownSeconds = 6f
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
        vehicle.ReportCollisionResistance(effectiveResistance);

        float energyCostMul = Mathf.Lerp(1f, 5f, liveResistance);
        float spoolUpSeconds = vehicle.profile != null ? vehicle.profile.boostSpoolUpSeconds : 0f;
        if (spoolUpSeconds > 0f)
        {
            float spoolUp01 = Mathf.Clamp01(vehicle.BoostHeldSeconds / spoolUpSeconds);
            energyCostMul = Mathf.Lerp(1f, energyCostMul, spoolUp01);
        }
        // drainResistance01: 0 = full drain rate, 1 = no drain (see MusicalRoleProfile.cs) — Lerp
        // must run from full (1.0) down to minimal (0.1) as drainRes rises, not the reverse.
        vehicle.DrainEnergy(drainPerSec * Mathf.Lerp(1.0f, 0.1f, drainRes) * energyCostMul * dt);

        if (effectiveResistance >= 1f) return;

        TriggerCarveEmissionSpike();
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
            // Highlight is now a continuous ramp tied to the contact envelope, not a fixed-
            // duration pulse — see TickCollisionHighlight (CosmicDust.VehicleCompression.cs).
        }

        if (Role == MusicalRole.None || Time.time < _nextDustPluckTime) return;
        PlayNonBoostDustPluck(vehicle);
    }

    private void PlayNonBoostDustPluck(Vehicle vehicle)
    {
        float hold01 = Mathf.Clamp01(_nonBoostClearSeconds / Mathf.Max(0.01f, pluck.swellSeconds));
        float bloom01 = hold01 * hold01;
        int durTicks = Mathf.RoundToInt(Mathf.Lerp(pluck.minDurationTicks, pluck.maxDurationTicks, bloom01));
        float vel127 = Mathf.Lerp(pluck.minVelocity127, pluck.maxVelocity127, bloom01);
        float cooldown = Mathf.Lerp(pluck.maxCooldownSeconds, pluck.minCooldownSeconds, bloom01);

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        _gfm?.controller?.PlayDustChordPluck(vehicle, Role, bloom01, 4, durTicks, vel127);
        // minCooldownSeconds must exceed the max single quantization step, or a cell could
        // re-arm and fire again before its first (now-delayed) pluck actually sounds.
        _nextDustPluckTime = Time.time + cooldown;
    }

    // Carve's distinct, non-color cue: a brief emission spike (obviously "more particles" for a
    // moment) instead of another tint pulse competing with the bump-pulse/regrow-glow/charge-pulse
    // channel. Uses the same tracked single-slot emission-multiplier lane as the grow-in ramp
    // (_emissionMulRoutine, CosmicDust.Lifecycle.cs) — safe here since carve only ever fires on an
    // already-settled, fully-grown cell, never mid grow-in.
    private void TriggerCarveEmissionSpike()
    {
        if (_emissionMulRoutine != null) { StopCoroutine(_emissionMulRoutine); _emissionMulRoutine = null; }
        _emissionMulRoutine = StartCoroutine(CarveEmissionSpikeRoutine());
    }

    private IEnumerator CarveEmissionSpikeRoutine()
    {
        yield return _particles.LerpEmissionMultiplier(_particles.CurrentMultiplier, 5f, 0.08f);
        yield return _particles.LerpEmissionMultiplier(5f, 1f, 0.35f);
        _emissionMulRoutine = null;
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
