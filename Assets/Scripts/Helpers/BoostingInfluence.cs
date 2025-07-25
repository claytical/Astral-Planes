using UnityEngine;
[CreateAssetMenu(menuName = "Astral Planes/Drums/Boosting Influence")]
public class BoostingInfluence : DrumVolumeInfluence
{
    public override float EvaluateVolume(DrumTrack drumTrack)
    {
        int boostingPlayers = 0;
        int totalPlayers = 0;

        foreach (var player in GameFlowManager.Instance.localPlayers)
        {
            if (player?.plane == null) continue;
            totalPlayers++;
            if (player.plane.boosting) boostingPlayers++;
        }

        if (totalPlayers == 0) return 0f;
        return boostingPlayers / (float)totalPlayers;
    }
}
