using System.Linq;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Drums/Track Density Influence")]
public class DensityInfluence : DrumVolumeInfluence
{
    public int densityThreshold = 4;

    public override float EvaluateVolume(DrumTrack drumTrack)
    {
        var controller = drumTrack.trackController;
        if (controller == null) return 0f;

        float total = controller.tracks.Length;
        float active = controller.tracks.Count(t => t.GetNoteDensity() >= densityThreshold);
        return active / total;
    }
}
