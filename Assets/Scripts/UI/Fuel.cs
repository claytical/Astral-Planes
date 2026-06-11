using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("UI/Vehicle Stat Bars")]
public class Fuel : MonoBehaviour
{
    [Header("Runtime Fuel")]
    public Image fuelFill;

    public void UpdateFuelUI(float energyRatio)
    {
        if (fuelFill != null)
            fuelFill.fillAmount = Mathf.Clamp01(energyRatio);
    }
}
