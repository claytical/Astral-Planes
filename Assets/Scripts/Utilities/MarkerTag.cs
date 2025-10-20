// MarkerTag.cs
using UnityEngine;

public class MarkerTag : MonoBehaviour
{
    public InstrumentTrack track;
    public int step;
    public int burstId;       // 0 = not collected / placeholder
    public bool isPlaceholder;
}