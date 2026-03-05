using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName="Astral/Phase Chapter Library")]
public class PhaseChapterLibrary : ScriptableObject
{
    [Serializable]
    public class Chapter
    {
        public MazeArchetype phase;
        public List<MotifProfile> motifs = new();
        public bool loopMotifs = true;
    }

    public List<Chapter> chapters = new();

    public Chapter Get(MazeArchetype phase)
        => chapters.Find(c => c.phase == phase);
}