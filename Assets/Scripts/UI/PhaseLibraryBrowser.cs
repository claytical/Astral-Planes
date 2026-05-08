using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Root MonoBehaviour for the PhaseLibrary scene.
/// Loads saved ring records and populates one card per completed motif under its phase container.
/// </summary>
public class PhaseLibraryBrowser : MonoBehaviour
{
    [SerializeField] private MotifRecordCard cardPrefab;

    [Tooltip("One container per phase index. Records whose PhaseIndex exceeds the array length are skipped.")]
    [SerializeField] private Transform[] phaseContainers;

    [Tooltip("Scene to load when a card is selected (typically TrackSelection).")]
    [SerializeField] private string nextSceneName = "TrackSelection";

    private void Start()
    {
        PopulateCards(RingSessionStore.LoadAllRingsFromDisk());
    }

    private void PopulateCards(List<MotifSnapshot> rings)
    {
        foreach (var snap in rings)
        {
            int pi = snap.PhaseIndex;
            if (phaseContainers == null || pi < 0 || pi >= phaseContainers.Length) continue;
            if (phaseContainers[pi] == null) continue;

            var card = Instantiate(cardPrefab, phaseContainers[pi]);
            card.Setup(snap, OnCardSelected);
        }
    }

    private void OnCardSelected(int phaseIdx, int motifIdx)
    {
        PhaseLibraryStartConfig.RequestStart(phaseIdx, motifIdx);
        SceneManager.LoadScene(nextSceneName);
    }
}
