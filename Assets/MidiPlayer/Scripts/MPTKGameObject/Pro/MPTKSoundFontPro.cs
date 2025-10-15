using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using static UnityEngine.Networking.UnityWebRequest;
namespace MidiPlayerTK
{
    public partial class MPTKSoundFont
    {
        // @cond NODOC
        /// <summary>
        /// Contains a dictionary with all samples when using an internal soundfont different than the selected.
        /// Null if internal selected soundfont is used (soundfont from MidiPlayerGlobal).
        /// </summary>
        public LocalDicAudioWave DicAudioWaveLocal = null;
        // @endcond

        /// <summary>@brief
        /// Call every 200 ms when download is in progress. See also #ProgressValue
        /// @code
        ///   midiSynth.MPTK_SoundFont.ProgressCallback = (float progress) =>
        ///   {
        ///         // Warning - this callback is not running inside the Unity thread.
        ///         // It's not possible to use Unity API. So forget to directly update the UI.
        ///         // One exception, Debug is authorized! But variable can be set and reuse in the Update() ... for example.
        ///         // The progress percentage provided by Unity SendWebRequest seems not a percentage. So, I removed the % character ...
        ///         Debug.Log($"<color=yellow>In Progress {midiSynth.name} {progress * 100:F0}</color>");
        ///   };
        /// @endcode
        /// </summary>
        public Action<float> ProgressCallback = null;

        /// <summary>@brief
        /// Provide a value for the progress of the download. 
        /// Normalized between 0 and 1, should be a percentage but webRequest seems to provide a value not related to real percentage.
        /// </summary>
        public float ProgressValue { get => progressValue; }
        private float progressValue;

        /// <summary>@brief
        /// Define a callback when the SoundFont is loaded and ready to use.
        /// @code
        /// // This callback will be triggered when the soundfont is ready.
        /// midiSynth.MPTK_SoundFont.LoadedCallback = (MidiSynth synth) =>
        /// {
        ///     SoundfontIsReady(synth);
        /// };
        /// @endcode
        /// </summary>
        public Action<MidiSynth> LoadedCallback = null;

        /// <summary>@brief
        /// Provide the status of the SoundFont loaded.
        /// </summary>
        public LoadingStatusSoundFontEnum StatusSoundFont { get => statusSoundFont; }
        private LoadingStatusSoundFontEnum statusSoundFont;

        /// <summary>@brief
        /// Time to download the SoundFont from a web resource or a local file.
        /// </summary>
        public TimeSpan TimeToDownloadSoundFont { get => timeToDownloadSoundFont; }
        
        /// <summary>@brief
        /// Time to load the SoundFont in MidiSynth prefab.
        /// </summary>
        public TimeSpan TimeToLoadSoundFont { get => timeToLoadSoundFont; }
        
        /// <summary>@brief
        /// Time to loas samples in the MidiSynth.
        /// </summary>
        public TimeSpan TimeToLoadWave { get => timeToLoadWave; }

        /// <summary>@brief
        /// Total time to load the SoundFont.
        /// </summary>
        public TimeSpan TimeToLoadOverall { get => timeToLoadOverall; }

        private TimeSpan timeToDownloadSoundFont = TimeSpan.Zero;
        private TimeSpan timeToLoadSoundFont = TimeSpan.Zero;
        private TimeSpan timeToLoadWave = TimeSpan.Zero;
        private TimeSpan timeToLoadOverall = TimeSpan.Zero;

        private string soundFile;
        private System.Diagnostics.Stopwatch watchLoadSF;
        private SFLoad sfLoad = null;

        /// <summary>@brief
        /// Load a SoundFont file on the fly when the application is running (MPTK Pro).
        /// <param name="pathSF">The path to the SoundFont, loading from:
        /// - Local desktop  when path starts with file:// 
        /// - a web resource when starts with http:// or https://
        /// - else from the internal MPTK soundfont DB with this name,
        /// - if null or empty, the default internal soundfont is selected.
        /// </param>
        /// <returns>
        ///     - true if loading is in progress. Defined LoadedCallback to get information when loading is complete.
        ///     - false if an error is detected in the parameters. The callback LoadedCallback is not called if the return value is false.
        /// </returns>
        /// @code
        /// List<MidiSynth> midiSynths = new List<MidiSynth>();
        /// midiSynths.AddRange(FindObjectsByType<MidiFilePlayer>(FindObjectsSortMode.None));
        /// foreach (MidiSynth midiSynth in midiSynths)
        /// {
        ///     midiSynth.MPTK_SoundFont.ProgressCallback = (float progress) =>
        ///     {
        ///         SoundfontInProgress(progress, midiSynth);
        ///     };
        ///
        ///
        ///     // This callback will be triggered when the soundfont is ready.
        ///     midiSynth.MPTK_SoundFont.LoadedCallback = (MidiSynth synth) =>
        ///     {
        ///         SoundfontIsReady(synth);
        ///     };
        ///
        ///     // Prepare switching between SoundFonts for each synths selected.
        ///     // Here we load the SoundFont from the URI defined in the UI.
        ///     // Set download options defined in the UI. 
        ///     // -------------------------------------------------------------------
        ///     midiSynth.MPTK_SoundFont.LoadFromCache = True;
        ///     midiSynth.MPTK_SoundFont.SaveToCache = True;
        ///     midiSynth.MPTK_SoundFont.DownloadOnly = False;
        ///
        ///     bool result = midiSynth.MPTK_SoundFont.Load("https://mptkapi.paxstellar.com/GeneralUser-GS-v2.0.1.sf2");
        ///     if (!result)
        ///         Debug.Log($"TestLoadSF - Download canceled, status:{MidiPlayerGlobal.MPTK_StatusLastSoundFontLoaded} URI:{InputURLSoundFontAtRun.text}");
        /// }
        /// @endcode
        public bool Load(string path = null)
        {
            //MidiPlayerGlobal.MPTK_SoundFontLoaded = false;
            bool result = false;
            timeToDownloadSoundFont = TimeSpan.Zero;
            timeToLoadSoundFont = TimeSpan.Zero;
            timeToLoadWave = TimeSpan.Zero;
            timeToLoadOverall = TimeSpan.Zero;
            watchLoadSF = new System.Diagnostics.Stopwatch(); // High resolution time
            watchLoadSF.Start();
            if (string.IsNullOrEmpty(path))
            {
                // Load from the default internal soundfont
                return LoadDefaultSoundfont();
            }

            string pathSf = path.ToLower();
            if (pathSf.StartsWith("https://") || pathSf.StartsWith("http://") || pathSf.StartsWith("file://"))
            {
                // Load external SF from a web resource or a local file
                if (synth.VerboseSoundfont) Debug.Log($"Load - Switch to an external soundfont '{path}' for '{synth.name}'");
                Routine.RunCoroutine(loadFromUri(path), Segment.RealtimeUpdate);
                result = true; // but waiting the callback to get the definitive result
            }
            else
            {
                // Load from a local MPTK resource
                result = switchToInternal(path);
                if (result)
                {
                    bool midiPlayerIsPaused = PauseMidiSynth();
                    synth.MPTK_StopSynth();
                    synth.MPTK_InitSynth(preserveActivVoice: false);
                    isInternal = true;
                    isDefault = false;
                    UnPauseMidiSynth(midiPlayerIsPaused);
                }
                else
                {
                    statusSoundFont = LoadingStatusSoundFontEnum.SoundFontNotLoaded;
                }
                timeToLoadOverall = timeToDownloadSoundFont + timeToLoadSoundFont + timeToLoadWave;
                LoadedCallback?.Invoke(synth);

            }
            return result;
        }

        // @cond NODOC  // until end

        private bool LoadDefaultSoundfont()
        {
            bool result;
            if (MidiPlayerGlobal.CurrentMidiSet != null && MidiPlayerGlobal.CurrentMidiSet.ActiveSounFontInfo != null && MidiPlayerGlobal.ImSFCurrent != null)
            {
                if (synth.VerboseSoundfont) Debug.Log($"Load - Switch to the internal default soundfont '{MidiPlayerGlobal.ImSFCurrent.SoundFontName}' for '{synth.name}'.");
                sfLocal = null;
                DicAudioWaveLocal = null;
                isInternal = true;
                isDefault = true;
                synth.MPTK_StopSynth();
                synth.MPTK_InitSynth(preserveActivVoice: false);
                statusSoundFont = LoadingStatusSoundFontEnum.Success;
                //MidiPlayerGlobal.MPTK_SoundFontLoaded = true;
                result = true;
            }
            else
            {
                Debug.LogWarning($"Load - Failed to switch to the default internal MPTK soundfont, for '{synth.name}'.");
                statusSoundFont = LoadingStatusSoundFontEnum.SoundFontNotLoaded;
                result = false;
            }
            timeToLoadSoundFont = watchLoadSF.Elapsed;
            timeToLoadOverall = timeToDownloadSoundFont + timeToLoadSoundFont + timeToLoadWave;
            LoadedCallback?.Invoke(synth);
            return result;
        }



        // Switch soundfont to a soundfont defined in Maestro MPTK (store in Unity resources).
        private bool switchToInternal(string sfName)
        {

            // If sfName is defined, load the soundfont from the Resources folder
            string pathToImSF = MidiPlayerGlobal.SoundfontsDB + "/" + sfName;
            ImSoundFont loaded = ImSoundFont.LoadMPTKSoundFont(pathToImSF, sfName);

            timeToDownloadSoundFont = TimeSpan.Zero;
            timeToLoadSoundFont = watchLoadSF.Elapsed;
            watchLoadSF.Restart();

            if (loaded == null)
            {
                Debug.LogWarning($"Failed to switch to the internal soundfont '{sfName}' for MidiSynth '{synth.name}'.");
                return false;
            }
            if (synth.VerboseSoundfont) Debug.Log($"Switch to an internal soundfont '{sfName}' for MidiSynth '{synth.name}'.");
            sfLocal = loaded;
            soundFontName = sfName;
            DicAudioWaveLocal = new LocalDicAudioWave(sfName);
            if (!DicAudioWaveLocal.SamplesLoaded)
            {
                foreach (HiSample smpl in SoundFont.HiSf.Samples)
                {
                    string WavePath = Path.Combine(pathToImSF + "/", MidiPlayerGlobal.PathToWave);
                    string path = WavePath + "/" + Path.GetFileNameWithoutExtension(smpl.Name);

                    AudioClip ac = Resources.Load<AudioClip>(path);
                    if (ac != null)
                    {
                        //DicAudioClip.Add(smpl.Name, ac);
                        float[] data = new float[ac.samples * ac.channels];
                        if (ac.GetData(data, 0))
                        {
                            smpl.SampleRate = (uint)ac.frequency;
                            smpl.Data = data;
                            DicAudioWaveLocal.Add(smpl);
                            if (synth.VerboseSample) Debug.Log($"{smpl.Name:20} added sample {smpl.SampleRate} Hz {smpl.Data.Length} bytes.");
                        }
                        else
                        {
                            Debug.LogWarning($"{smpl.Name} data GetData not there.");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"Could not load sample clip {path}");
                    }
                }
                DicAudioWaveLocal.SamplesLoaded = true;
                timeToLoadWave = watchLoadSF.Elapsed;
                watchLoadSF.Restart();
            }

            BuildBankList();
            BuildPresetList(true);
            BuildPresetList(false);
            return true;
        }


        // Switch soundfont to a loaded soundfont
        // equivalent to CreateSoundFont
        private bool switchToExternal(SFLoad sf)
        {
            if (synth.VerboseSoundfont) Debug.Log($"switchToExternal - Load soundfont from SFLoad {sf.SfData.fname} for synth {synth.name}");

            sfLocal = new ImSoundFont();
            sfLocal.HiSf = sf.SfData;
            sfLocal.LiveSF = true;

            // Build bank content
            sfLocal.Banks = new ImBank[ImSoundFont.MAXBANKPRESET];
            foreach (HiPreset p in sfLocal.HiSf.preset)
            {
                if (p != null)
                {
                    if (sfLocal.Banks[p.Bank] == null)
                    {
                        // New bank, create it
                        sfLocal.Banks[p.Bank] = new ImBank()
                        {
                            BankNumber = p.Bank,
                            defpresets = new HiPreset[ImSoundFont.MAXBANKPRESET]
                        };
                    }
                    sfLocal.Banks[p.Bank].defpresets[p.Num] = p;
                }
            }

            foreach (ImBank bank in sfLocal.Banks)
            {
                if (bank != null)
                {
                    bank.PatchCount = 0;
                    foreach (HiPreset preset in bank.defpresets)
                    {
                        if (preset != null)
                        {
                            // Bank count
                            bank.PatchCount++;
                        }
                    }
                }
            }

            sfLocal.DefaultBankNumber = defaultBank < 0 ? sfLocal.FirstBank() : defaultBank;
            sfLocal.DrumKitBankNumber = drumBank < 0 ? sfLocal.LastBank() : drumBank;
            sfLocal.LiveSF = true;

            BuildBankList();
            BuildPresetList(true);
            BuildPresetList(false);

            System.Diagnostics.Stopwatch watchLoadWave = new System.Diagnostics.Stopwatch(); // High resolution time
            watchLoadWave.Start();
            sfLocal.SamplesData = new float[sfLocal.HiSf.SampleData.Length / 2];
            int size = sfLocal.HiSf.SampleData.Length / 2 - 1;
            for (int i = 0, j = 0; i <= size; i++, j += 2)
                sfLocal.SamplesData[i] = ((short)((sfLocal.HiSf.SampleData[j + 1] << 8) | sfLocal.HiSf.SampleData[j])) / 32768.0F;
            timeToLoadWave = watchLoadWave.Elapsed;
            isInternal = false;

            return true;
        }

        /// <summary>@brief
        /// Build list of presets found in the SoundFont
        /// </summary>
        private void BuildBankList()
        {
           // listBank = new List<MPTKListItem>();
            banksName = new List<string>();
            banksNumber = new List<int>();
            try
            {
                //Debug.Log(">>> Load Preset - b:" + ibank + " p:" + ipatch);
                if (sfLocal != null)
                {
                    foreach (ImBank bank in sfLocal.Banks)
                    {
                        if (bank != null)
                        {
                            //listBank.Add(new MPTKListItem() { Index = bank.BankNumber, Label = "Bank " + bank.BankNumber });
                            banksName.Add(bank.BankNumber + " - Bank");
                            banksNumber.Add(bank.BankNumber);
                        }
                        //else
                        //    listBank.Add(null);
                    }
                }
                else
                {
                    Debug.Log(MidiPlayerGlobal.ErrorNoSoundFont);
                }
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }


        /// <summary>@brief
        /// Build list of presets found in the default bank of the current SoundFont.
        /// </summary>
        private void BuildPresetList(bool forInstrument)
        {
            //List<MPTKListItem> presets = new List<MPTKListItem>();
            List<string> names;
            List<int> numbers;

            if (forInstrument)
            {
                presetsName = new List<string>();
                presetsNumber = new List<int>();
                names = presetsName;
                numbers = presetsNumber;
            }
            else
            {
                presetsDrumName = new List<string>();
                presetsDrumNumber = new List<int>();
                names = presetsDrumName;
                numbers = presetsDrumNumber;
            }
            try
            {
                //Debug.Log(">>> Load Preset - b:" + ibank + " p:" + ipatch);
                if (sfLocal != null)
                {
                    if ((forInstrument && sfLocal.DefaultBankNumber >= 0 && sfLocal.DefaultBankNumber < sfLocal.Banks.Length) ||
                       (!forInstrument && sfLocal.DrumKitBankNumber >= 0 && sfLocal.DrumKitBankNumber < sfLocal.Banks.Length))
                    {
                        int ibank = forInstrument ? sfLocal.DefaultBankNumber : sfLocal.DrumKitBankNumber;
                        if (sfLocal.Banks[ibank] != null)
                        {
                            sfLocal.Banks[ibank].PatchCount = 0;
                            for (int ipreset = 0; ipreset < sfLocal.Banks[ibank].defpresets.Length; ipreset++)
                            {
                                HiPreset p = sfLocal.Banks[ibank].defpresets[ipreset];
                                if (p != null)
                                {
                                    string label = $"{p.Num} - {p.Name}";
                                    //presets.Add(new MPTKListItem() { Index = p.Num, Label = label, Position = presets.Count });
                                    names.Add(label);
                                    numbers.Add(p.Num);
                                    sfLocal.Banks[ibank].PatchCount++;
                                }
                            }
                        }
                        else
                        {
                            Debug.LogWarningFormat("Default bank {0} for {1} is not defined.", ibank, forInstrument ? "Instrument" : "Drum");
                        }
                    }
                    else
                    {
                        Debug.LogWarningFormat("No default bank defined for {0}.", forInstrument ? "Instrument" : "Drum");
                    }

                    // Global count
                    foreach (ImBank bank in sfLocal.Banks)
                    {
                        if (bank != null)
                        {
                            bank.PatchCount = 0;
                            foreach (HiPreset preset in bank.defpresets)
                            {
                                if (preset != null)
                                {
                                    // Bank count
                                    bank.PatchCount++;
                                }
                            }
                            //ImSFCurrent.PatchCount += bank.PatchCount;
                        }
                    }
                }
                else
                {
                    Debug.Log(MidiPlayerGlobal.ErrorNoSoundFont);
                }
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            //if (forInstrument)
            //    listPresetInstrument = presets;
            //else
            //    listPresetDrum = presets;
        }

        // Load a SoundFont file on the fly when the application is running from a local file or from a web resource.
        private IEnumerator<float> loadFromUri(string uriSf)
        {
            if (synth.VerboseSoundfont) Debug.Log($"loadFromUri - '{uriSf}'");
            statusSoundFont = LoadingStatusSoundFontEnum.InProgress;

            if (string.IsNullOrEmpty(uriSf))
            {
                statusSoundFont = LoadingStatusSoundFontEnum.InvalidURL;
                Debug.LogWarning("loadFromUri: SoundFont path not defined");
                yield return 0;
            }
            else if (!uriSf.ToLower().StartsWith("file://") &&
                     !uriSf.ToLower().StartsWith("http://") &&
                     !uriSf.ToLower().StartsWith("https://"))
            {
                statusSoundFont = LoadingStatusSoundFontEnum.InvalidURL;
                Debug.LogWarning("loadFromUri: path to SoundFont must start with file:// or http:// or https:// - found: '" + uriSf + "'");
                yield return 0;
            }
            else
            {
                Uri uri = new Uri(uriSf);
                soundFontName = Uri.UnescapeDataString(uri.Segments.Last());
                string soundPath = MidiPlayerGlobal.MPTK_PathSoundFontCache;
                if (!Directory.Exists(soundPath)) Directory.CreateDirectory(soundPath);
                soundFile = Path.Combine(soundPath, soundFontName);

                byte[] sfData = null;
                if (LoadFromCache && File.Exists(soundFile))
                {
                    if (synth.VerboseSoundfont) Debug.Log($"loadFromUri - load from cache '{soundFontName}'");
                    try
                    {
                        sfData = File.ReadAllBytes(soundFile);
                        loadFromBytes(sfData); // with invoke LoadedCallback
                    }
                    catch (System.Exception ex)
                    {
                        statusSoundFont = LoadingStatusSoundFontEnum.SoundFontNotLoaded;
                        MidiPlayerGlobal.ErrorDetail(ex);
                    }
                }
                else
                {
                    if (synth.VerboseSoundfont) Debug.Log($"loadFromUri - Download start '{soundFontName}'");
                    // with invoke LoadedCallback
                    yield return Routine.WaitUntilDone(Routine.RunCoroutine(DownloadFileAsyncToMemory(uri, loadFromBytes, HandleError, ProgressCallback), Segment.RealtimeUpdate));
                }

                if (synth.VerboseSoundfont) Debug.Log($"loadFromUri - Download end '{soundFontName}'");
                yield return 0;
            }
        }

        void loadFromBytes(byte[] data)
        {
            if (synth.VerboseSoundfont) Debug.Log($"loadFromBytes - Length {data.Length} bytes");
            try
            {
                timeToDownloadSoundFont = watchLoadSF.Elapsed;
                watchLoadSF.Restart();

                if (SaveToCache)
                {
                    if (synth.VerboseSoundfont) Debug.Log($"loadFromBytes - Save to cache '{soundFile}'");
                    File.WriteAllBytes(soundFile, data);
                }
                if (DownloadOnly)
                {
                    if (synth.VerboseSoundfont) Debug.Log($"loadFromBytes - Download only, soundfont not loaded to the MidiSynth '{synth.name}'");
                    statusSoundFont = LoadingStatusSoundFontEnum.Success;
                }
                else
                {
                    sfLoad = new SFLoad(data, SFFile.SfSource.SF2);
                    if (sfLoad != null)
                    {
                        synth.MPTK_StopSynth();
                        bool midiPlayerIsPaused = PauseMidiSynth();
                        switchToExternal(sfLoad);
                        synth.MPTK_InitSynth(preserveActivVoice: false);
                        UnPauseMidiSynth(midiPlayerIsPaused);

                        statusSoundFont = LoadingStatusSoundFontEnum.Success;
                    }
                }
            }
            catch (System.Exception ex)
            {
                statusSoundFont = LoadingStatusSoundFontEnum.SoundFontNotLoaded;
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            timeToLoadSoundFont = watchLoadSF.Elapsed;
            timeToLoadOverall = timeToDownloadSoundFont + timeToLoadSoundFont + timeToLoadWave;
            LoadedCallback?.Invoke(synth);
        }

        void HandleError(LoadingStatusSoundFontEnum err)
        {
            Debug.Log("Download failed: " + err);
            statusSoundFont = err;
            LoadedCallback?.Invoke(synth);
        }

        private bool PauseMidiSynth()
        {
            bool paused = false;
            try
            {
                if (synth is MidiFilePlayer)
                {
                    if (synth.VerboseSoundfont) Debug.Log($"PauseMidiSynth - '{synth.name}'");
                    MidiFilePlayer player = (MidiFilePlayer)synth;
                    if (player.MPTK_IsPlaying)
                    {
                        player.MPTK_Pause(); // pause MIDI sequencer
                        paused = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            return paused;
        }

        private void UnPauseMidiSynth(bool unPausePlayer)
        {
            try
            {
                if (unPausePlayer)
                {
                    MidiFilePlayer player = (MidiFilePlayer)synth;
                    if (synth.VerboseSoundfont) Debug.Log($"UnPauseMidiSynth - '{synth.name}' was at position {player.MPTK_Position} {player.MPTK_TickCurrent}");
                    player.MPTK_StartSequencerMidi();
                    // Seems useless, but re-apply from the MIDI start all MIDI events that are not note-on (controller, tempo, preset change, ...)
                    player.MPTK_TickCurrent = player.MPTK_TickCurrent;
                    player.MPTK_UnPause();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private IEnumerator<float> DownloadFileAsyncToMemory(Uri uri, Action<byte[]> onComplete, Action<LoadingStatusSoundFontEnum> onError, Action<float> onProgress = null)
        {
            System.Diagnostics.Stopwatch watchLoadSF = new System.Diagnostics.Stopwatch(); // High resolution time
            using (UnityWebRequest req = UnityWebRequest.Get(uri))
            {
                UnityWebRequestAsyncOperation op = req.SendWebRequest();

                while (!op.isDone)
                {
                    progressValue = op.progress;
                    onProgress?.Invoke(op.progress);
                    //Debug.Log($"{op.progress} {req.result} {req.error}");
                    yield return Routine.WaitForSeconds(0.2f);
                }
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        byte[] data = req.downloadHandler.data;
                        if (data == null)
                            onError?.Invoke(LoadingStatusSoundFontEnum.SoundFontEmpty);
                        else if (data.Length < 4 || System.Text.Encoding.Default.GetString(data, 0, 4) != "RIFF")
                            onError?.Invoke(LoadingStatusSoundFontEnum.NoRIFFSignature);
                        else
                            onComplete?.Invoke(data);
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke(LoadingStatusSoundFontEnum.SoundFontNotLoaded);
                        Debug.LogException(ex);
                    }
                }
                else
                {
                    onError?.Invoke(LoadingStatusSoundFontEnum.NetworkError);
                    //Debug.LogWarning("Network error - " + uri);
                }

                if (synth.VerboseSoundfont) Debug.Log($"DownloadFileAsyncToMemory - '{synth.name}' result:{req.result}");

                yield return 0;
            }
        }

        private class HiSampleDictionary : Dictionary<string, HiSample>
        {
            public void AddOrUpdateSample(string key, HiSample sample)
            {
                this[key] = sample;
            }

            public bool TryGetSampleByName(string name, out HiSample sample)
            {
                return this.TryGetValue(name, out sample);
            }
        }

        public class LocalDicAudioWave
        {
            public bool SamplesLoaded = false;
            private HiSampleDictionary dicWave;
            private static Dictionary<string, HiSampleDictionary> dicSf;

            public LocalDicAudioWave()
            {

            }
            public LocalDicAudioWave(string sfName)
            {

                if (dicSf == null)
                {
                    dicSf = new Dictionary<string, HiSampleDictionary>();
                }
                if (!dicSf.TryGetValue(sfName, out dicWave))
                {
                    dicWave = new HiSampleDictionary();
                    dicSf[sfName] = dicWave;
                    SamplesLoaded = false;
                    Debug.Log($"Create new samples dict for {sfName}");
                }
                else
                {
                    SamplesLoaded = true;
                    Debug.Log($"Sample dict already exist, samples: {dicWave.Keys.Count}");
                }
            }


            public bool Check()
            {
                return (dicWave == null || dicWave.Count == 0 ? false : true);
            }

            public void Add(HiSample smpl)
            {
                HiSample c;
                try
                {
                    if (dicWave != null && !dicWave.TryGetValue(smpl.Name, out c))
                    {
#if DEBUG_LOAD_WAVE
                    Debug.Log($"DicAudioWave.Add {dicWave.Count} {smpl.Name} {smpl.SampleRate} {smpl.End - smpl.Start}");
#endif
                        dicWave.Add(smpl.Name, smpl);
                    }
                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }
            }
            public bool Exist(string name)
            {
                try
                {
                    if (dicWave != null)
                    {
                        HiSample c;
                        return dicWave.TryGetValue(name, out c);
                    }
                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }
                return false;
            }
            public HiSample Get(string name)
            {
                try
                {
                    HiSample c;
                    dicWave.TryGetValue(name, out c);
                    return c;
                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }
                return null;
            }
            public HiSample GetWave(string name)
            {
                try
                {
                    HiSample c;

                    dicWave.TryGetValue(name, out c);
                    return c;
                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }
                return null;
            }
        }
        //! @endcond
    }
}
