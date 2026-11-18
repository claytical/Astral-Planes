using UnityEngine;

[CreateAssetMenu(
    fileName = "RoleMotifNoteSetConfig",
    menuName = "Astral Planes/Music/Motif/Role Motif NoteSet Config")]
public class RoleMotifNoteSetConfig : ScriptableObject
{
    [Header("Identity")]
    public string id;
    public MusicalRoleProfile roleProfile;
    public MusicalRole role => roleProfile != null ? roleProfile.role : MusicalRole.None;

    [Header("Authored Riff")]
    public RiffAsset riff;
    
    [Tooltip("Number of loop boundaries after spawn before the discovery node expires if not captured. When > 0 this WINS over the role's DiscoveryTrackNodeLocomotionProfile.expireAfterLoops; 0 = fall back to that profile.")]
    [Min(0)]
    public int mineNodeExpireAfterLoops = 3;

    [Header("Note Ascension")]
    [Tooltip("How many drum loops collected notes take to rise to the ascension line (before the per-extra-bin bonus). 0 = use InstrumentTrackConfig.defaultAscendLoops.")]
    [Min(0)]
    public int ascendLoops = 0;
}