using System.Collections.Generic;
using UnityEngine;
using MidiPlayerTK;

public class SequenceManager : MonoBehaviour
{
    [System.Serializable]
    public class MidiInstrument
    {
        public string name;
        public MidiFilePlayer filePlayer;
        public int preset;
        public int bank;
        public float volume = 1.0f;
    }

    public List<MidiInstrument> midiInstruments;
    public int BPM = 120;
}
