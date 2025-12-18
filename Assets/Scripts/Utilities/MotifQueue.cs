using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Motif Queue")]
public class MotifQueue : ScriptableObject
{
    [Tooltip("Ordered list of motifs the session will progress through.")]
    public List<MotifProfile> motifs = new List<MotifProfile>();
}