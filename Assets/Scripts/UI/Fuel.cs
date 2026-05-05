using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("UI/Vehicle Stat Bars (Fuel Legacy)")]
public class Fuel : MonoBehaviour
{
    // Legacy name: this component now manages both runtime fuel and player-select stat bars.
    // It can live on a single GameObject and drive Image refs anywhere in the canvas.

    [Header("Primary Fill")]
    public Image fuelFill;

    [Header("Player Select Comparison Fills (same component, assign any 4 Images)")]
    public Image capacityFill;
    public Image efficiencyFill;
    public Image speedFill;
    public Image boostFill;

    public void UpdateFuelUI(float energyRatio)
    {
        if (fuelFill != null)
        {
            fuelFill.fillAmount = Mathf.Clamp01(energyRatio);
        }
    }

    public void UpdateProfileComparisonUI(float capacityRatio, float fuelEfficiencyRatio, float speedRatio, float boostRatio)
    {
        if (capacityFill != null)
        {
            capacityFill.fillAmount = Mathf.Clamp01(capacityRatio);
        }

        if (efficiencyFill != null)
        {
            efficiencyFill.fillAmount = Mathf.Clamp01(fuelEfficiencyRatio);
        }

        if (speedFill != null)
        {
            speedFill.fillAmount = Mathf.Clamp01(speedRatio);
        }

        if (boostFill != null)
        {
            boostFill.fillAmount = Mathf.Clamp01(boostRatio);
        }
    }
}
