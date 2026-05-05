using UnityEngine;
using UnityEngine.UI;

public class Fuel : MonoBehaviour
{
    [Header("Primary Fill")]
    public Image fuelFill;

    [Header("Player Select Comparison Fills")]
    public Image efficiencyFill;
    public Image speedFill;

    public void UpdateFuelUI(float energyRatio)
    {
        if (fuelFill != null)
        {
            fuelFill.fillAmount = Mathf.Clamp01(energyRatio);
        }
    }

    public void UpdateProfileComparisonUI(float fuelEfficiencyRatio, float speedRatio)
    {
        if (efficiencyFill != null)
        {
            efficiencyFill.fillAmount = Mathf.Clamp01(fuelEfficiencyRatio);
        }

        if (speedFill != null)
        {
            speedFill.fillAmount = Mathf.Clamp01(speedRatio);
        }
    }
}
