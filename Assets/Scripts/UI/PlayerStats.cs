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
        vehicleIcon.sprite = vehicle.GetComponent<SpriteRenderer>().sprite;
        UpdateFuel(currentVehicle.capacity, currentVehicle.capacity);
        Debug.Log("Energy collected should be displayed.");
    }

    public void SetColor(Color color)
    {
        vehicleIcon.color = color;
    }

    public void Deactivate()
    {
        inactivePanel.SetActive(true);
    }
    
    public void UpdateFuel(float currentEnergy, float maxEnergy)
    {
        float ratio = currentEnergy / maxEnergy;
        fuel.UpdateFuelUI(ratio);
    }

    // Optional: Method to recreate or reset the vehicle using the stored ID
    public Vehicle RecreateVehicle()
    {
        // Logic to recreate the vehicle based on the stored vehicle ID
        // This will depend on your game's architecture and vehicle management system
        // Example (assuming you have a vehicle manager):
        // return VehicleManager.Instance.GetVehicleByID(vehicleID);

        return null; // Placeholder return, replace with actual logic
    }
}
