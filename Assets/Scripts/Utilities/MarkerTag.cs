// MarkerTag.cs
using UnityEngine;

public class MarkerTag : MonoBehaviour
{
    public InstrumentTrack track;
    public int step = -1;

    public int burstId = -1;
    public int ascendBurstId = -1;

    public bool isPlaceholder = false;
    public bool isAscending = false;

    private void Reset()
    {
        step = -1;
        burstId = -1;
        ascendBurstId = -1;
        isPlaceholder = false;
        isAscending = false;
    }

    private void Awake()
    {
        // In case prefab/scene instances have zeroed ints:
        if (step == 0 && track == null) step = -1;
        if (burstId == 0) burstId = -1;
        if (ascendBurstId == 0) ascendBurstId = -1;
    }
}
