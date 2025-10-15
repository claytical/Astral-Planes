// TracksBundleAnchor.cs
using UnityEngine;

public sealed class TracksBundleAnchor : MonoBehaviour
{
    public PhaseTransitionManager phaseTransitionManager;
    public DrumTrack drumTrack;
    public InstrumentTrackController controller;
    public CosmicDustGenerator dustGenerator;
    public MineNodeProgressionManager progressionManager;
    public SpawnGrid spawnGrid;
    public GlitchManager glitchManager;
    public ChordChangeArpeggiator arp;

    void Reset() => Cache();
    void Awake() { if (!IsCached()) Cache(); }

    void OnEnable()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm) gfm.RegisterTracksBundle(this);
    }

    void OnDisable()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm) gfm.UnregisterTracksBundle(this);
    }

    void Cache()
    {
        // Cache from self or children; includes inactive objects
        phaseTransitionManager = phaseTransitionManager ?? GetComponentInChildren<PhaseTransitionManager>(true);
        drumTrack              = drumTrack              ?? GetComponentInChildren<DrumTrack>(true);
        controller             = controller             ?? GetComponentInChildren<InstrumentTrackController>(true);
        dustGenerator          = dustGenerator          ?? GetComponentInChildren<CosmicDustGenerator>(true);
        progressionManager     = progressionManager     ?? GetComponentInChildren<MineNodeProgressionManager>(true);
        spawnGrid              = spawnGrid              ?? GetComponentInChildren<SpawnGrid>(true);
        glitchManager          = glitchManager          ?? GetComponentInChildren<GlitchManager>(true);
        arp                    = arp                    ?? GetComponentInChildren<ChordChangeArpeggiator>(true);
    }

    bool IsCached() =>
        phaseTransitionManager && drumTrack && controller && dustGenerator &&
        progressionManager && spawnGrid && glitchManager && arp;
}