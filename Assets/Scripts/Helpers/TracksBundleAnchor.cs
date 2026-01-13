using System.Collections;
using UnityEngine;

public sealed class TracksBundleAnchor : MonoBehaviour
{
    public PhaseTransitionManager phaseTransitionManager;
    public DrumTrack drumTrack;
    public InstrumentTrackController controller;
    public CosmicDustGenerator dustGenerator;
    public SpawnGrid spawnGrid;
    public GlitchManager glitchManager;

    bool _cached;
    bool _setupRequested;

    void Reset() => Cache();

    void Awake()
    {
        if (!_cached) Cache();
    }

    IEnumerator Start()
    {
        // Let other scene objects (e.g. PlayerStatsGridAnchor) run Awake/Start first
        yield return null;

        var gfm = GameFlowManager.Instance;
        if (!gfm) yield break;

        // Ensure bundle is registered once per scene load
        gfm.RegisterTracksBundle(this);

        if (!_setupRequested)
        {
            _setupRequested = true;
            gfm.BeginGeneratedTrackSetup();   // new method in GameFlowManager
        }
    }

    void OnDisable()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm) gfm.UnregisterTracksBundle(this);
    }

    void Cache()
    {
        phaseTransitionManager = phaseTransitionManager ?? GetComponentInChildren<PhaseTransitionManager>(true);
        drumTrack              = drumTrack              ?? GetComponentInChildren<DrumTrack>(true);
        controller             = controller             ?? GetComponentInChildren<InstrumentTrackController>(true);
        dustGenerator          = dustGenerator          ?? GetComponentInChildren<CosmicDustGenerator>(true);
        spawnGrid              = spawnGrid              ?? GetComponentInChildren<SpawnGrid>(true);
        glitchManager          = glitchManager          ?? GetComponentInChildren<GlitchManager>(true);
        _cached = phaseTransitionManager && drumTrack && controller && dustGenerator &&
                  spawnGrid && glitchManager;
    }
    
}
