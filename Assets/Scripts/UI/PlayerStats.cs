using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerStats : MonoBehaviour
{
    public Image vehicleIcon;
    public Fuel fuel;
    private Vehicle _currentVehicle;

    public void SetStats(Vehicle vehicle)
    {
        // Store the vehicle ID and reference to the vehicle instance
        _currentVehicle = vehicle;
        if (vehicle != null)
        {
            vehicleIcon.sprite = vehicle.GetComponent<SpriteRenderer>().sprite;
            UpdateFuel(_currentVehicle.capacity, _currentVehicle.capacity);
        }
        else {Debug.LogWarning("There is no vehicle attached to this object.");}
        
        Debug.Log("Energy collected should be displayed.");
    }
    public void UpdateFuel(float currentEnergy, float maxEnergy)
    {
        float ratio = currentEnergy / maxEnergy;
        fuel.UpdateFuelUI(ratio);
    }
    public void SetColor(Color color)
    {
        vehicleIcon.color = color;
    }


}
