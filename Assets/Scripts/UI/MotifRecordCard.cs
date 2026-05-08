using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays a single completed-motif record as an animated mini-ring.
/// Instantiated and initialized by PhaseLibraryBrowser.
/// </summary>
public class MotifRecordCard : MonoBehaviour
{
    [SerializeField] private MotifRingGlyphApplicator ringApplicator;
    [SerializeField] private Button selectButton;
    [SerializeField] private Text   label;

    private MotifSnapshot         _snapshot;
    private Action<int, int>      _onSelected;

    /// <summary>
    /// Bind snapshot data and selection callback. Call once after instantiation.
    /// </summary>
    public void Setup(MotifSnapshot snapshot, Action<int, int> onSelected)
    {
        _snapshot   = snapshot;
        _onSelected = onSelected;

        if (label != null)
            label.text = $"Phase {snapshot.PhaseIndex + 1}  •  Motif {snapshot.MotifIndex + 1}";

        if (ringApplicator != null)
            ringApplicator.AnimateApply(snapshot);

        if (selectButton != null)
        {
            selectButton.onClick.RemoveAllListeners();
            selectButton.onClick.AddListener(() => _onSelected?.Invoke(_snapshot.PhaseIndex, _snapshot.MotifIndex));
        }
    }
}
