using UnityEngine;
using UnityEngine.UI;

public class Fuel : MonoBehaviour
{
    [Header("Primary Fill")]
    public Image fuelFill;

    [Header("Player Select Comparison Fills")]
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
