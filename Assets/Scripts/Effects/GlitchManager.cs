using UnityEngine;
using Kino;
using System.Collections;

public class GlitchManager : MonoBehaviour
{
    public AnalogGlitch analog;
    public DigitalGlitch digital;

    private bool[] toggles = new bool[4];

    public void ToggleEffect(int index)
    {
        toggles[index] = !toggles[index];
        UpdateEffects();
    }

    void UpdateEffects()
    {
        analog.scanLineJitter = toggles[0] ? 0.2f : 0f;
        analog.verticalJump = toggles[1] ? 0.3f : 0f;
        digital.intensity = toggles[2] ? 0.5f : 0f;
        analog.colorDrift = toggles[3] ? 0.25f : 0f;
    }
}