using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MidiPlayerTK;
using UnityEngine;

/// <summary>
/// Renders saved rings to WAV by playing them through a scene RingPreviewPlayer (the real MPTK synth
/// and mixer, which can't render offline) and capturing the output in real time. Drums are off.
///
/// Per ring: {dir}/{baseName}.wav is the AudioListener mix (includes mixer-bus effects), and with
/// stems on, {dir}/Stems/{baseName} - {Role}.wav is each role's synth output (pre-bus). Every other
/// AudioSource in the scene is muted while the export runs so only the ring reaches the mix.
///
/// Driven from RingSessionManagerWindow in Play mode; OnWavWritten lets the editor encode MP3s.
/// </summary>
public class RingAudioExporter : MonoBehaviour
{
    public struct Job
    {
        public MotifSnapshot snap;
        public string baseName;
    }

    public struct Options
    {
        public int   loops;
        public float tailSec;
        public bool  stems;
    }

    public bool IsBusy { get; private set; }

    /// <summary>Fired on the main thread after each WAV is written (path).</summary>
    public event Action<string> OnWavWritten;

    /// <summary>(fraction 0–1, label) as the batch advances.</summary>
    public event Action<float, string> OnProgress;

    public event Action<int> OnFinished; // rings exported

    private bool _cancel;
    public void Cancel() => _cancel = true;

    private static RingAudioExporter _instance;

    public static RingAudioExporter GetOrCreate()
    {
        if (_instance != null) return _instance;
        var go = new GameObject("[RingAudioExporter]") { hideFlags = HideFlags.DontSave };
        DontDestroyOnLoad(go);
        return _instance = go.AddComponent<RingAudioExporter>();
    }

    public void Begin(RingPreviewPlayer player, List<Job> jobs, string dir, Options opts)
    {
        if (IsBusy || player == null || jobs == null || jobs.Count == 0) return;
        StartCoroutine(Run(player, jobs, dir, opts));
    }

    private IEnumerator Run(RingPreviewPlayer player, List<Job> jobs, string dir, Options opts)
    {
        IsBusy  = true;
        _cancel = false;

        bool prevDrums = player.DrumsEnabled;
        bool prevRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        player.DrumsEnabled = false;

        // One stem tap per distinct player (roles without their own bus share the template player).
        var roleSources = new Dictionary<MidiStreamPlayer, List<MusicalRole>>();
        foreach (var role in RingPlaybackPlan.RoleChannel.Keys)
        {
            var p = player.GetRolePlayer(role);
            if (p == null) continue;
            if (!roleSources.TryGetValue(p, out var list)) roleSources[p] = list = new List<MusicalRole>();
            list.Add(role);
        }

        var muted = MuteOtherSources(player, roleSources.Keys);

        var listener = FindAnyObjectByType<AudioListener>();
        WavCaptureTap mixTap = listener != null ? listener.gameObject.AddComponent<WavCaptureTap>() : null;
        if (mixTap == null) Debug.LogWarning("[RingAudioExporter] No AudioListener in scene — mix WAV skipped.");

        var stemTaps = new Dictionary<MidiStreamPlayer, WavCaptureTap>();
        if (opts.stems)
            foreach (var p in roleSources.Keys)
                stemTaps[p] = p.gameObject.AddComponent<WavCaptureTap>();

        int done = 0;
        try
        {
            for (int j = 0; j < jobs.Count && !_cancel; j++)
            {
                var job = jobs[j];
                OnProgress?.Invoke(j / (float)jobs.Count, job.baseName);

                player.Play(job.snap);
                if (!player.IsPlaying)
                {
                    Debug.LogWarning($"[RingAudioExporter] Could not play '{job.baseName}' — skipped.");
                    continue;
                }

                double startDsp = player.LoopStartDsp;
                double endDsp   = startDsp + Mathf.Max(1, opts.loops) * (double)player.LoopLengthSec;
                float  maxSec   = (float)(endDsp - AudioSettings.dspTime) + opts.tailSec + 1f;

                mixTap?.Arm(maxSec);
                foreach (var t in stemTaps.Values) t.Arm(maxSec);

                // Keep active roles from this ring's plan (player.ActiveRoles) for stem naming.
                var activeRoles = new HashSet<MusicalRole>(player.ActiveRoles);

                while (AudioSettings.dspTime < endDsp && !_cancel)
                {
                    float f = (float)((AudioSettings.dspTime - startDsp) / (endDsp - startDsp + opts.tailSec));
                    OnProgress?.Invoke((j + Mathf.Clamp01(f)) / jobs.Count, job.baseName);
                    yield return null;
                }

                player.StopFiring(); // let the last notes finish and release
                double tailEnd = endDsp + opts.tailSec;
                while (AudioSettings.dspTime < tailEnd && !_cancel) yield return null;

                if (_cancel) { DisarmAll(mixTap, stemTaps); player.Stop(); break; }

                Directory.CreateDirectory(dir);
                if (mixTap != null)
                    WriteTrimmed(mixTap, startDsp, endDsp + opts.tailSec, Path.Combine(dir, job.baseName + ".wav"));

                foreach (var kv in stemTaps)
                {
                    var roles = roleSources[kv.Key].FindAll(activeRoles.Contains);
                    if (roles.Count == 0) { kv.Value.Disarm(out _); continue; }
                    string stemDir = Path.Combine(dir, "Stems");
                    Directory.CreateDirectory(stemDir);
                    WriteTrimmed(kv.Value, startDsp, endDsp + opts.tailSec,
                        Path.Combine(stemDir, $"{job.baseName} - {string.Join("+", roles)}.wav"));
                }

                player.Stop();
                done++;
                yield return null;
            }
        }
        finally
        {
            DisarmAll(mixTap, stemTaps);
            if (mixTap != null) Destroy(mixTap);
            foreach (var t in stemTaps.Values) if (t != null) Destroy(t);
            foreach (var src in muted) if (src != null) src.mute = false;
            player.DrumsEnabled = prevDrums;
            Application.runInBackground = prevRunInBackground;
            IsBusy = false;
            OnProgress?.Invoke(1f, "");
            OnFinished?.Invoke(done);
        }
    }

    private static void DisarmAll(WavCaptureTap mix, Dictionary<MidiStreamPlayer, WavCaptureTap> stems)
    {
        mix?.Disarm(out _);
        foreach (var t in stems.Values) if (t != null) t.Disarm(out _);
    }

    // Trim the capture to [startDsp, endDsp] using the dsp time of its first buffer.
    private void WriteTrimmed(WavCaptureTap tap, double startDsp, double endDsp, string path)
    {
        double firstDsp = tap.FirstBufferDsp;
        var samples = tap.Disarm(out int count);
        if (samples == null || count == 0 || firstDsp < 0)
        {
            Debug.LogWarning($"[RingAudioExporter] No audio captured for {Path.GetFileName(path)}.");
            return;
        }

        int ch = tap.Channels;
        int sr = AudioSettings.outputSampleRate;
        long lead = (long)Math.Round((startDsp - firstDsp) * sr);
        if (lead < 0)
        {
            Debug.LogWarning($"[RingAudioExporter] Capture started {-lead / (float)sr:0.000}s after step 0 for {Path.GetFileName(path)}.");
            lead = 0;
        }
        long frames = (long)Math.Round((endDsp - startDsp) * sr);
        int offset  = (int)Math.Min(lead * ch, count);
        int length  = (int)Math.Min(frames * ch, count - offset);

        var outBuf = new float[length];
        Array.Copy(samples, offset, outBuf, 0, length);
        WavWriter.Write(path, outBuf, length, ch, sr);
        Debug.Log($"[RingAudioExporter] Wrote {path} ({length / (float)ch / sr:0.0}s)");
        OnWavWritten?.Invoke(path);
    }

    private static List<AudioSource> MuteOtherSources(RingPreviewPlayer player, IEnumerable<MidiStreamPlayer> keep)
    {
        // MIDI players keep their children (MPTK voice sources); the preview's own GameObject only
        // holds the (disabled) drum decks, so its children — other carousel UI — are muted.
        var keepRoots = new List<Transform>();
        foreach (var p in keep) if (p != null) keepRoots.Add(p.transform);

        var muted = new List<AudioSource>();
        foreach (var src in FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude))
        {
            if (src.mute || src.gameObject == player.gameObject
                || keepRoots.Exists(root => src.transform.IsChildOf(root))) continue;
            src.mute = true;
            muted.Add(src);
        }
        return muted;
    }
}
