using UnityEngine;
using Kino;
using System.Collections;

public class GlitchManager : MonoBehaviour
{
    public AnalogGlitch analog;
    private bool[] toggles = new bool[4];
    private int currentGlitchIndex = -1;
   
    public void ToggleEffect(int index)
    {
        if (index < 0 || index >= toggles.Length)
        {
            Debug.LogWarning($"⚠️ Invalid glitch effect index: {index}");
            return;
        }
        toggles[index] = !toggles[index];
        UpdateEffects();
    }
    public void ResetGlitches()
    {
        for (int i = 0; i < toggles.Length; i++)
            toggles[i] = false;
        currentGlitchIndex = -1;
        UpdateEffects();
    }

    void UpdateEffects()
    {
        analog.scanLineJitter = toggles[0] ? 0.1f : 0f;
        analog.verticalJump = toggles[1] ? 0.03f : 0f;
        analog.horizontalShake = toggles[2] ? 0.01f : 0f;
        analog.colorDrift = toggles[3] ? 0.15f : 0f;
    }
    public void SpikeAndDisableEffect(int index)
    {
        if (index < 0 || index >= 4) return;

        StartCoroutine(SpikeRoutine(index));
    }

    private IEnumerator SpikeRoutine(int index)
    {
        switch (index)
        {
            case 0: analog.scanLineJitter = 0.8f; break;
            case 1: analog.verticalJump = 0.9f; break;
            case 2: analog.horizontalShake = .02f; break;
            case 3: analog.colorDrift = 0.9f; break;
        }

        yield return new WaitForSeconds(0.3f);

        toggles[index] = false;
        UpdateEffects();
    }

}