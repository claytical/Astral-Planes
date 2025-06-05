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

    private Vehicle currentVehicle;



    public void SetStats(Vehicle vehicle)
    {
        // Store the vehicle ID and reference to the vehicle instance
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
        float ratio = currentEnergy / maxEnergy;
        fuel.UpdateFuelUI(ratio);
    }


}
