using System.Collections;
using UnityEngine;

public partial class LocalPlayer
{
    private static int _nextId;
    private readonly int _id = System.Threading.Interlocked.Increment(ref _nextId);

    public GameObject playerSelect;
    [SerializeField] private GameObject playerStatsUI;

    [Header("Tutorial UI")]
    [SerializeField] private ControlTutorialHighlight miniTutorialPrefab;
    [SerializeField] private Vector3 miniTutorialScale = new Vector3(0.5f, 0.5f, 1f);

    private ControlTutorialHighlight _miniTutorial;
    private bool _hasNavigatedSelectionOnce;

    // --- Dust spawn pocket / keep-clear ---
    // Use CosmicDustGenerator's ref-counted vehicle keep-clear to carve the spawn cell
    // before instantiating the vehicle, avoiding initial collider interpenetration.
    [Header("Spawn Pocket")]
    [Tooltip("Carve a dust-free pocket at the spawn cell before placing the vehicle.")]
    public bool carveSpawnPocket = true;

    [Tooltip("Radius (in dust grid cells) for the spawn pocket. 0 clears only the spawn cell.")]
    public int spawnPocketRadiusCells = 0;

    [Tooltip("Fade duration for spawn-pocket dust clearing.")]
    public float spawnPocketFadeSeconds = 0.02f;

    private Vector2Int _dustKeepClearCell;

    private Color _color;
    private PlayerStatsTracking _playerStats;

    public void CreatePlayerSelect()
    {
        // If we already had a selection UI from an earlier TrackSelection visit, kill it.
        if (_selection != null)
        {
            Destroy(_selection.gameObject);
            _selection = null;
        }

        if (_miniTutorial != null)
        {
            Destroy(_miniTutorial.gameObject);
            _miniTutorial = null;
        }

        _hasNavigatedSelectionOnce = false;

        GameObject ps = Instantiate(playerSelect);
        _selection = ps.GetComponent<PlayerSelect>();

        // Spawn mini controller and attach it near the player’s selection UI (exactly once)
        if (miniTutorialPrefab != null && _selection != null)
        {
// Choose a very specific anchor transform inside the PlayerSelectShip prefab.
            Transform miniAnchor =
                _selection.tutorialControls
                    ? _selection.tutorialControls.transform
                    : _selection.transform;

            _miniTutorial = Instantiate(miniTutorialPrefab); // instantiate unparented

            if (ControlTutorialDirector.Instance != null)
            {
                ControlTutorialDirector.Instance.RegisterMini(
                    lp: this,
                    mini: _miniTutorial,
                    parentOverride: miniAnchor,
                    localPos: Vector3.zero,
                    localScale: miniTutorialScale,
                    localRot: Quaternion.identity
                );
            }
        }
    }

    public void SetStats()
    {
        _ui?.SetStats(plane);
    }

    private void SetColor()
    {
        _color = _selection.planeIcon.color;
    }

    public void Launch()  // <- no params
    {
        if (_launched || _launchStarted) return;
        _launchStarted = true;
        StartCoroutine(LaunchWhenReady());
    }

    private void NotifySelectionNavigatedOnce()
    {
        if (_hasNavigatedSelectionOnce) return;
        _hasNavigatedSelectionOnce = true;

        if (ControlTutorialDirector.Instance != null)
            ControlTutorialDirector.Instance.Mini_SetConfirmStage(this);
    }

    private IEnumerator LaunchWhenReady()
    {
        // Wait for authoritative deps from GameFlowManager
        yield return new WaitUntil(() =>
                GameFlowManager.Instance &&
                GameFlowManager.Instance.PlayerStatsGrid &&               // UI parent exists
                GameFlowManager.Instance.activeDrumTrack &&               // drums ready
                GameFlowManager.Instance.controller &&                    // tracks configured
                GameFlowManager.Instance.harmony                          // HarmonyDirector bound
        );

        if (GameFlowManager.VerboseLogging) Debug.Log("[CRASH TEST] Track Ready");

        if (playerVehicle == null)
        {
            // Nothing to launch with (e.g. hangar exhausted out from under us).
            _launchStarted = false;
            yield break;
        }

        var gfm = GameFlowManager.Instance;

        // --- UI: player stats card under the grid (created once, reused across respawns)
        var grid = gfm.PlayerStatsGrid;
        if (_ui == null)
        {
            var statsUI = Instantiate(playerStatsUI, grid);
            _ui = statsUI.GetComponent<PlayerStats>();
        }
        int w = gfm.spawnGrid.gridWidth;

// pick a row near the bottom; tweak as you want
        int spawnY = 1;
        int spawnX = Random.Range(0, w);
        var spawnCell = new Vector2Int(spawnX, spawnY);
        // 🔹 NEW: place the vehicle at a random grid cell
        var drums = gfm.activeDrumTrack;
        var spawnGrid = gfm.spawnGrid;

        if (drums != null && spawnGrid != null)
        {
            // --- NEW: carve a safe spawn cell BEFORE we place/instantiate the vehicle ---
            if (carveSpawnPocket && gfm.dustGenerator != null)
            {
                _dustKeepClearOwnerId = _id;
                _dustKeepClearCell = spawnCell;

                gfm.dustGenerator.SetVehicleKeepClear(
                    ownerId: _dustKeepClearOwnerId,
                    centerCell: _dustKeepClearCell,
                    radiusCells: Mathf.Max(0, spawnPocketRadiusCells),
                    forceRemoveExisting: true,
                    forceRemoveFadeSeconds: Mathf.Max(0.01f, spawnPocketFadeSeconds)
                );
                _dustKeepClearActive = true;
            }

// Snap LocalPlayer to that grid cell in world space
            Vector3 spawnWorld = drums.GridToWorldPosition(spawnCell);
            transform.position = spawnWorld;

// Optionally mark the cell as occupied so dust never spawns here
            gfm.spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.Node);
            // --- Vehicle
            var vehicleGO = Instantiate(playerVehicle, transform);
            plane = vehicleGO.GetComponent<Vehicle>();
            if (plane != null)
                gfm.RegisterVehicle(plane);
        }

        // Player stats plumbing
        _playerStats = GetComponent<PlayerStatsTracking>();
        if (plane)
        {
            plane.playerStats   = _playerStats;
            plane.playerStatsUI = _ui;
            plane.SyncEnergyUI();
            plane.SetDrumTrack(drums);

            var sr = plane.GetComponent<SpriteRenderer>();
            if (sr) sr.color = _color;
            SetStats();
            _ui.SetColor(_color);
            _playerInput.SwitchCurrentActionMap("Play");
        }

        _launched = true;
    }

    public string GetSelectedShipName()
    {
        return _selection?.GetCurrentShipName();
    }
}
