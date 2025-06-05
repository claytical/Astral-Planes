using System.Collections;
using UnityEngine;
    
public class EnergyWaveEffect : MonoBehaviour
{
    public GameObject energyPrefab;
    public void TriggerWave(Vector3 position, float timeUntilBeatChange)
    {
        GameObject wave = Instantiate(energyPrefab, position, Quaternion.identity);
        ParticleSystem ps = wave.GetComponent<ParticleSystem>();
        var main = ps.main;
        main.loop = false;
        main.duration = timeUntilBeatChange;
        main.startLifetime = timeUntilBeatChange;
        ps.Play();
    }

}