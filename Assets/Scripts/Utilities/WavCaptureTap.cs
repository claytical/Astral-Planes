using UnityEngine;

/// <summary>
/// Records whatever passes through this GameObject's audio filter chain while armed. Added after a
/// MidiSynth it captures that synth's output; on the AudioListener it captures the final mix.
/// Used by RingAudioExporter. Remove the component when done — it costs a copy per buffer while armed.
/// </summary>
public class WavCaptureTap : MonoBehaviour
{
    private readonly object _lock = new();
    private float[] _buffer;
    private int     _count;
    private bool    _armed;

    /// <summary>dsp time of the first captured buffer, or -1 until one arrives.</summary>
    public double FirstBufferDsp { get; private set; } = -1;
    public int Channels { get; private set; } = 2;

    /// <summary>Start capturing into a fresh buffer sized for maxSeconds (excess is dropped).</summary>
    public void Arm(float maxSeconds)
    {
        int cap = Mathf.CeilToInt(maxSeconds * AudioSettings.outputSampleRate) * 2; // stereo; resized if more channels arrive
        lock (_lock)
        {
            _buffer = new float[Mathf.Max(cap, 1024)];
            _count = 0;
            FirstBufferDsp = -1;
            _armed = true;
        }
    }

    /// <summary>Stop capturing and return the samples (interleaved) and their count.</summary>
    public float[] Disarm(out int count)
    {
        lock (_lock)
        {
            _armed = false;
            count = _count;
            var b = _buffer;
            _buffer = null;
            return b;
        }
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        lock (_lock)
        {
            if (!_armed || _buffer == null) return;
            if (FirstBufferDsp < 0)
            {
                FirstBufferDsp = AudioSettings.dspTime;
                Channels = channels;
                if (channels != 2)
                    _buffer = new float[_buffer.Length / 2 * channels];
            }
            int n = Mathf.Min(data.Length, _buffer.Length - _count);
            if (n <= 0) return;
            System.Array.Copy(data, 0, _buffer, _count, n);
            _count += n;
        }
    }
}
