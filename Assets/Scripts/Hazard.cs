using UnityEngine;
using System;

public class Hazard : MonoBehaviour
{
    public float energyAbsorbed = 0f;
    public float maxEnergy = 3f;
    public GameObject starPrefab;
    private DrumTrack drumTrack;

    public void AbsorbEnergy(float amount)
    {
        energyAbsorbed += amount;
        if (energyAbsorbed >= maxEnergy)
        {
            TransformIntoStar();
        }
    }

    public void SetDrumTrack(DrumTrack track)
    {
        drumTrack = track;
    }    private void TransformIntoStar()
    {
        Debug.Log("Black Hole transformed into a Star!");
        GameObject go = Instantiate(starPrefab, transform.position, Quaternion.identity);
        DrumLoopCollectable star = go.GetComponent<DrumLoopCollectable>();
        if (star != null)
        {
            star.SetDrumTrack(drumTrack);
        }
        Destroy(gameObject);
    }
}