using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ShipMusicalProfile", menuName = "Astral Planes/Ship Musical Profile")]
public class ShipMusicalProfile : ScriptableObject
{
    [Header("Identity & Music")]
    public string shipName;
    [Tooltip("A brief description of the ship's musical personality")]
    [TextArea(2, 4)] public string description;

    [Header("Movement (Arcade RB2D)")]
    public float arcadeMaxSpeed = 14f;
    public float arcadeAccel = 10f;
    public float arcadeBoostAccel = 70f;
    public float arcadeLinearDamping = 0.10f;
    public float arcadeAngularDamping = 0.50f;

    [Header("Coast / Stop")]
    public float coastBrakeForce = 1.0f;
    public float stopSpeed = 0.03f;
    public float stopAngularSpeed = 5f;
    public float inputDeadzone = 0.20f;
    
    [Header("Dust Carving (Boost)")]
    [Tooltip("Carve radius multiplier when boosting. 1 = default.")]
    [Range(0.25f, 3f)] public float carveRadiusMul = 1.0f;
    [Tooltip("Carve power multiplier when boosting. 1 = default.")]
    [Range(0.25f, 5f)] public float carvePowerMul = 1.0f;

    [Header("Physics")]
    public float mass = 1.5f;

    [Header("Fuel Tradeoffs")]
    public float capacity = 10f;              // tank size (Vehicle energyLevel starts here)
    [Range(0.25f, 2f)] public float burnEfficiency = 1.0f; // multiplies baseBurnAmount for this ship

    [Header("Ecological Role")]
    [Tooltip("Half-width of the forward plow in grid cells. 0 = plow disabled.")]
    public int plowHalfWidthCells = 1;
    [Tooltip("Depth (forward reach) of the plow in grid cells.")]
    public int plowDepthCells = 2;
    [Tooltip("Minimum speed (world units/s) before the plow activates.")]
    public float plowMinSpeed = 2f;
    [Tooltip("Radius around the vehicle kept clear of dust (cells). 0 = use Inspector value.")]
    public int vehicleKeepClearRadiusCells = 0;
}
