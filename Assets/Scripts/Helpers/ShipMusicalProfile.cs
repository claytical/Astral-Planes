using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ShipMusicalProfile", menuName = "Astral Planes/Ship Musical Profile")]
public class ShipMusicalProfile : ScriptableObject
{
    [Header("Identity & Music")]
    public string shipName;
    [Tooltip("List of allowed General MIDI presets for this ship")]
    public List<int> allowedMidiPresets = new List<int>();
    [Tooltip("A brief description of the ship's musical personality")]
    [TextArea(2, 4)] public string description;

    [Header("Movement (Arcade RB2D)")]
    public float arcadeMaxSpeed = 14f;
    public float arcadeAccel = 10f;
    public float arcadeBoostAccel = 80f;
    public float arcadeLinearDamping = 0.10f;
    public float arcadeAngularDamping = 0.50f;
    public bool  requireBoostForThrust = false;

    [Header("Coast / Stop")]
    public float coastBrakeForce = 1.0f;
    public float stopSpeed = 0.03f;
    public float stopAngularSpeed = 5f;
    public float inputDeadzone = 0.20f;

    [Header("Environment Bias")]
    [Range(0.5f,1f)] public float envScaleFloor = 0.60f; // dust can't slow below this

    [Header("Dust Carving (Boost)")]
    [Tooltip("Multiplier applied to CosmicDustGenerator.vehicleErodeRadius when boosting. 1 = default.")]
    [Range(0.25f, 3f)] public float carveRadiusMul = 1.0f;
    [Tooltip("Multiplier applied to CosmicDustGenerator.vehicleErodePerTick budget when boosting. 1 = default.")]
    [Range(0.25f, 5f)] public float carvePowerMul = 1.0f;

    [Header("Physics")]
    public float mass = 1.5f;

    [Header("Fuel Tradeoffs")]
    public float capacity = 10f;              // tank size (Vehicle energyLevel starts here)
    [Range(0.25f, 2f)] public float burnEfficiency = 1.0f; // multiplies baseBurnAmount for this ship
}
