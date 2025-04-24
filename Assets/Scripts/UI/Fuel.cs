using UnityEngine;
using UnityEngine.UI;

public class Fuel : MonoBehaviour
{
    public Image fuelFill;

    public void UpdateFuelUI(float energyRatio)
    {
        if (fuelFill != null)
        {
            fuelFill.fillAmount = Mathf.Clamp01(energyRatio);
        }
    }
}