using System.IO;
using UnityEngine;

/// <summary>Writes interleaved float samples as a 24-bit PCM WAV (lossless; Bandcamp-friendly).</summary>
public static class WavWriter
{
    public static void Write(string path, float[] samples, int count, int channels, int sampleRate)
    {
        const int bytesPerSample = 3;
        int dataBytes = count * bytesPerSample;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w  = new BinaryWriter(fs);

        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { 'W', 'A', 'V', 'E' });

        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                                         // fmt chunk size
        w.Write((short)1);                                   // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bytesPerSample);     // byte rate
        w.Write((short)(channels * bytesPerSample));         // block align
        w.Write((short)(bytesPerSample * 8));                // bits per sample

        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);

        var buf = new byte[dataBytes];
        for (int i = 0, o = 0; i < count; i++, o += 3)
        {
            int v = Mathf.RoundToInt(Mathf.Clamp(samples[i], -1f, 1f) * 8388607f);
            buf[o]     = (byte)v;
            buf[o + 1] = (byte)(v >> 8);
            buf[o + 2] = (byte)(v >> 16);
        }
        w.Write(buf);
    }
}
