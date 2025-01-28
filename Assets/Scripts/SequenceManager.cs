using UnityEngine;
using MidiPlayerTK;

public class SequenceManager : MonoBehaviour
{
    [System.Serializable]
    public class MidiInstrument
    {
        public MidiFilePlayer filePlayer; // The MIDI file player
        public int instrumentIndex; // Index of the instrument (e.g., 0, 1, 2 for instrumentPlayers)
        public float volume = .5f;
        public int preset;
        public int bank;
    }

    public MidiInstrument[] midiInstruments; // Array to define file-to-instrument assignments
    public int BPM;
    public float drumVolume = .5f;

    public DrumPatternType drumPattern; // Enum to specify the drum pattern

    public enum DrumPatternType
    {
        Heartbeat,
        Silence,
        SkipKick,
        Rock,
        Custom
    }
}