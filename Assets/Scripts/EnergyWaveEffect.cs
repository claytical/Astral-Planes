using System.Collections;
using UnityEngine;
    
public class EnergyWaveEffect : MonoBehaviour
{
    public Material waveMaterial;
    public float waveDuration = 1.5f;
    private float waveStrength = 0f;
    private bool isWaving = false;

    public void TriggerWave(Vector3 position)
    {
        StartCoroutine(AnimateWave(position));
    }

    private IEnumerator AnimateWave(Vector3 position)
    {
        isWaving = true;
        float elapsedTime = 0f;

        while (elapsedTime < waveDuration)
        {
            waveStrength = Mathf.Sin((elapsedTime / waveDuration) * Mathf.PI) * 0.5f;
            waveMaterial.SetFloat("_WaveStrength", waveStrength);
            waveMaterial.SetVector("_WaveOrigin", position);
            elapsedTime += Time.deltaTime;
            yield return null;
        }

        waveMaterial.SetFloat("_WaveStrength", 0f);
        isWaving = false;
    }
}