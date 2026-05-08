using TMPro;
using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("UI/Vehicle Stat Bars")]
public class Fuel : MonoBehaviour
{
    [Header("Runtime Fuel")]
    public Image fuelFill;

    [Header("Player Select Stat Bars")]
    public Image capacityBar;
    public Image speedBar;
    public TMP_Text boostText;
    public TMP_Text efficiencyText;

    public void UpdateFuelUI(float energyRatio)
    {
        if (fuelFill != null)
            fuelFill.fillAmount = Mathf.Clamp01(energyRatio);
    }

    public void UpdateSelectStats(float capacityRatio, float speedRatio, int boostPct, int efficiencyPct)
    {
        if (capacityBar != null) capacityBar.fillAmount = Mathf.Clamp01(capacityRatio);
        if (speedBar != null) speedBar.fillAmount = Mathf.Clamp01(speedRatio);
        if (boostText != null) boostText.text = $"{boostPct}%";
        if (efficiencyText != null) efficiencyText.text = $"{efficiencyPct}%";
    }
}
