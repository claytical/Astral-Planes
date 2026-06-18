using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MidiPlayerTK
{
    /// @ingroup midistreamplayer_chord_tools
    /// <summary>
    /// [MPTK PRO] Build Scale and Play with MidiStreamPlayer.\n
    /// 
    /// See example in TestMidiStream.cs and ExtStreamPlayerPro.cs
    /// @code
    ///
    ///     // Need a reference to the prefab MidiStreamPlayer you have added in your scene hierarchy.
    ///     public MidiStreamPlayer midiStreamPlayer;
    ///     
    ///     new void Start()
    ///     {
    ///         // Find the MidiStreamPlayer. Could be also set directly from the inspector.
    ///         midiStreamPlayer = FindObjectOfType<MidiStreamPlayer>();
    ///     }
    ///
    ///     private void PlayScale()
    ///     {
    ///         // get the current scale selected
    ///         MPTKRangeLib range = MPTKRangeLib.Range(CurrentScale, true);
    ///         for (int ecart = 0; ecart < range.Count; ecart++)
    ///         {
    ///             NotePlaying = new MPTKEvent()
    ///             {
    ///                 Command = MPTKCommand.NoteOn, // midi command
    ///                 Value = CurrentNote + range[ecart], // from 0 to 127, 48 for C4, 60 for C5, ...
    ///                 Channel = StreamChannel, // from 0 to 15, 9 reserved for drum
    ///                 Duration = DelayPlayScale, // note duration in millisecond, -1 to play indefinitely, MPTK_StopEvent to stop
    ///                 Velocity = Velocity, // from 0 to 127, sound can vary depending on the velocity
    ///                 Delay = ecart * DelayPlayScale, // delau in millisecond before playing the note
    ///             };
    ///             midiStreamPlayer.MPTK_PlayEvent(NotePlaying);
    ///         }
    ///     }
    /// @endcode
    /// </summary>
    public class MPTKRangeLib
    {
        /// <summary>@brief
        /// Position in the list (from the library)
        /// </summary>
        public int Index;

        /// <summary>@brief
        /// Long name of the scale
        /// </summary>
        public string Name;

        /// <summary>@brief
        /// Short name of the scale
        /// </summary>
        public string Short;

        /// <summary>@brief
        /// Some indicator when available.
        /// @li   M = major scale
        /// @li   m = minor scale
        /// @li   _ = undetermined
        /// </summary>
        public string Flag;

        /// <summary>@brief
        /// Common scale if true else exotic
        /// </summary>
        public bool Main;

        /// <summary>@brief
        /// Count of notes in the range
        /// </summary>
        public int Count;

        /// <summary>@brief
        /// Delta in 1/2 ton from the tonic, so first position (index=0) always return 0 regardless the range selected. 
        /// </summary>
        /// <param name="index">Position in the scale. If greater than count of notes in the scale, the delta in 1/2 tons is taken from the next octave.</param>
        /// <returns>Delta in 1/2 ton from the tonic</returns>
        public int this[int index]
        {
            get
            {
                if (Count == 0) return 0;
                if (octave == null) BuildOctave();
                int delta = 0;
                try
                {
                    delta = octave[index % Count] + ((index / Count) * 12);

                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }
                return delta;
            }
        }

        private int[] octave;

        /// <summary>@brief
        /// A full scale is based on 12 1/2 tons. This array contains 1/2 tons selected for the scale.
        /// </summary>
        private string[] position;

        private static List<MPTKRangeLib> scales;

        /// <summary>@brief
        /// Get a scale from an index. Scales are read from GammeDefinition.csv in folder Resources/GeneratorTemplate.csv.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        public static MPTKRangeLib Range(int index, bool log = false)
        {
            if (scales == null) Init(log);
            if (index < 0 && index >= scales.Count) return null;
            scales[index].BuildOctave(log);
            return scales[index];
        }

        /// <summary>@brief
        /// Get a scale from an index. Scales are read from GammeDefinition.csv in folder Resources/GeneratorTemplate.csv.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        public static MPTKRangeLib Range(MPTKRangeName index, bool log = false)
        {
            if (scales == null) Init(log);
            scales[(int)index].BuildOctave(log);
            return scales[(int)index];
        }

        /// <summary>@brief
        /// Count of scales availables in the library GammeDefinition.csv in folder Resources/GeneratorTemplate.csv
        /// </summary>
        public static int RangeCount
        {
            get
            {
                if (scales == null) Init();
                return scales.Count;
            }
        }

        private static void Init(bool log = false)
        {
            if (scales == null)
            {
                scales = new List<MPTKRangeLib>();
                TextAsset mytxtData = Resources.Load<TextAsset>("GeneratorTemplate/GammeDefinition");
                string text = System.Text.Encoding.UTF8.GetString(mytxtData.bytes);
                string[] list1 = text.Split('\r');
                if (list1.Length >= 1)
                {
                    for (int i = 1; i < list1.Length; i++)
                    {
                        string[] c = list1[i].Split(';');
                        if (c.Length >= 15)
                        {
                            MPTKRangeLib scale = new MPTKRangeLib();
                            try
                            {
                                scale.Index = scales.Count;
                                scale.Name = c[0];
                                if (scale.Name[0] == '\n') scale.Name = scale.Name.Remove(0, 1);
                                scale.Short = c[1];
                                scale.Flag = c[2];
                                scale.Main = (c[3].ToUpper() == "X") ? true : false;
                                scale.Count = Convert.ToInt32(c[4]);
                                scale.position = new string[12];
                                for (int j = 5; j <= 16; j++)
                                {
                                    scale.position[j - 5] = c[j];
                                }
                            }
                            catch (System.Exception ex)
                            {
                                MidiPlayerGlobal.ErrorDetail(ex);
                            }
                            scales.Add(scale);
                        }
                    }

                }
                if (log)
                    Debug.Log("Ranges loaded: " + MPTKRangeLib.scales.Count);
            }
        }

        private void BuildOctave(bool log = false)
        {
            if (octave == null)
            {
                try
                {
                    octave = new int[Count];
                    int iEcart = 0;
                    int vEcart = 1;
                    octave[0] = 0;
                    iEcart++;
                    for (int i = 1; i < position.Length; i++)
                    {
                        if (position[i].Trim().Length == 0)
                        {
                            vEcart++;
                        }
                        else
                        {
                            octave[iEcart] = vEcart;
                            iEcart++;
                            vEcart += 1;
                        }
                    }
                    //octave[octave.Length - 1] = 12;
                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }

                if (log)
                {
                    string info = string.Format("Range:{0} '{1}'", Flag, Name);
                    foreach (int e in octave)
                        info += string.Format(" [{0} {1}]", e, HelperNoteLabel.LabelFromMidi(48 + e));
                    Debug.Log(info);
                }
            }
        }
    }
}
