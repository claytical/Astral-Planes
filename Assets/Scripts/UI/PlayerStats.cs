using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;

public class PlayerStats : MonoBehaviour
{
    public Image vehicleIcon;
    public Fuel fuel;
    public TextMeshProUGUI collected;
    public GameObject inactivePanel;

    private int vehicleID;  // Store the vehicle ID to recreate the vehicle later
    private Vehicle currentVehicle;



    public void SetStats(Vehicle vehicle)
    {
        // Store the vehicle ID and reference to the vehicle instance
        vehicleID = vehicle.GetInstanceID();
        currentVehicle = vehicle;
        if (vehicle != null)
        {
            vehicleIcon.sprite = vehicle.GetComponent<SpriteRenderer>().sprite;
            UpdateFuel(currentVehicle.capacity, currentVehicle.capacity);
        }
        else {Debug.LogWarning("There is no vehicle attached to this object.");}
        
        Debug.Log("Energy collected should be displayed.");
    }

    public void SetColor(Color color)
    {
        vehicleIcon.color = color;
    }
    
    public void UpdateFuel(float currentEnergy, float maxEnergy)
    {
        Debug.Log($"current energy: {currentEnergy}, max energy: {maxEnergy}");
        float ratio = currentEnergy / maxEnergy;
        fuel.UpdateFuelUI(ratio);
    }


}
