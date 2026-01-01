// MarkerTag.cs
using UnityEngine;

public class MarkerTag : MonoBehaviour
{
    public InstrumentTrack track;
    public int step;
    public int burstId;       // 0 = not collected / placeholder
    public bool isPlaceholder;
    public bool isAscending = false;
    public int  ascendBurstId = -1;
}