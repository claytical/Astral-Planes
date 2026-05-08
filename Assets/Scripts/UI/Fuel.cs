using TMPro;
using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("UI/Vehicle Stat Bars")]
public class Fuel : MonoBehaviour
{
    [Header("Runtime Fuel")]
    public Image fuelFill;

    [Header("Player Select — Storage")]
    public Image capacityBar;
    public TMP_Text burnRateText;

    [Header("Player Select — Speed Bars")]
    public Image accelBar;
    public Image speedBar;
    public Image boostBar;

    public void UpdateFuelUI(float energyRatio)
    {
        if (fuelFill != null)
            fuelFill.fillAmount = Mathf.Clamp01(energyRatio);
    }

    public void UpdateSelectStats(
        float capacityRatio,
        float accelRatio,
        float speedRatio,
        float boostRatio,
        string burnRateLabel)
    {
        if (capacityBar != null) capacityBar.fillAmount = Mathf.Clamp01(capacityRatio);
        if (accelBar != null) accelBar.fillAmount = Mathf.Clamp01(accelRatio);
        if (speedBar != null) speedBar.fillAmount = Mathf.Clamp01(speedRatio);
        if (boostBar != null) boostBar.fillAmount = Mathf.Clamp01(boostRatio);
        if (burnRateText != null) burnRateText.text = burnRateLabel;
    }
}
