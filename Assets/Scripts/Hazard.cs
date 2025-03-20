using UnityEngine;
using System;

public class Hazard : MonoBehaviour
{
    public float energyAbsorbed = 0f;
    public float maxEnergy = 3f;
    public GameObject starPrefab;
    public NoteSet noteSet;
    public enum MinedObjectType { ChordChange, RootShift, NoteBehavior, RhythmStyle }
    private DrumTrack drumTrack;
    private InstrumentTrack track;
    public void AbsorbEnergy(float amount)
    {
        energyAbsorbed += amount;
        if (energyAbsorbed >= maxEnergy)
        {
        }
    }

    public void SetDrumTrack(DrumTrack drums)
    {
        drumTrack = drums;
        track = drumTrack.trackController.GetRandomTrack();
    }   

}