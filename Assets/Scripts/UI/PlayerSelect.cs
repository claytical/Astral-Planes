using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerSelect : MonoBehaviour
{
    public Image planeIcon;
    [Tooltip("Vehicle image under the Groove Style parent; mirrors planeIcon so the ship stays visible in the groove stage.")]
    public Image vehicleIcon;
    public VehicleStatDisplay statDisplay;
    public GameObject controlsAndStats;
    public GameObject tutorialControls;
    public Image border;
    public TMP_Text modelNameLabel;
    [Tooltip("Text line below the stat chart; D-pad up/down cycles the note placement method.")]
    public TMP_Text placementMethodLabel;
    [Tooltip("Description line under the placement mode name.")]
    public TMP_Text placementDescriptionLabel;
    [Tooltip("'Vehicle' parent: vehicle selection UI (shown during the vehicle stage and the confirmed state).")]
    public GameObject vehicleStage;
    [Tooltip("'Groove Style' parent: placement method UI (shown during the groove stage only).")]
    public GameObject grooveStyleStage;
    [Range(0f, 1f)] public float confirmedAlpha = 0.4f;

    private int _selectedColor = 0;
    private Hangar _hangar;
    private GameObject _chosenPlane;
    private string _currentShipName;
    private NotePlacementMethod _placementMethod = NotePlacementMethod.Auto;

    public NotePlacementMethod CurrentPlacementMethod => _placementMethod;

    public enum SelectStage { Vehicle, GrooveStyle }
    public SelectStage Stage { get; private set; } = SelectStage.Vehicle;

    private void ApplyStageVisuals()
    {
        if (vehicleStage != null) vehicleStage.SetActive(Stage == SelectStage.Vehicle);
        if (grooveStyleStage != null) grooveStyleStage.SetActive(Stage == SelectStage.GrooveStyle);
    }

    /// <summary>Vehicle -> Groove Style. Returns false if already past the vehicle stage or no vehicle is chosen.</summary>
    public bool AdvanceStage()
    {
        if (Stage != SelectStage.Vehicle || _chosenPlane == null) return false;
        Stage = SelectStage.GrooveStyle;
        ApplyStageVisuals();
        return true;
    }

    /// <summary>Groove Style -> Vehicle. Returns false if already at the vehicle stage.</summary>
    public bool StepBackStage()
    {
        if (Stage != SelectStage.GrooveStyle) return false;
        Stage = SelectStage.Vehicle;
        ApplyStageVisuals();
        return true;
    }

    /// <summary>D-pad left/right: cycles ships in the vehicle stage, placement mode in the groove stage.</summary>
    public void Navigate(int dir)
    {
        if (Stage == SelectStage.Vehicle)
        {
            if (dir >= 0) NextVehicle(); else PreviousVehicle();
        }
        else
        {
            CyclePlacementMethod();
        }
    }

    public void SetPlacementMethod(NotePlacementMethod method)
    {
        _placementMethod = method;
        RefreshPlacementLabel();
    }

    public void CyclePlacementMethod()
    {
        SetPlacementMethod(_placementMethod == NotePlacementMethod.Manual
            ? NotePlacementMethod.Auto
            : NotePlacementMethod.Manual);
    }

    private void RefreshPlacementLabel()
    {
        bool manual = _placementMethod == NotePlacementMethod.Manual;
        if (placementMethodLabel != null)
            placementMethodLabel.text = manual ? "Groove Commander" : "Cozy Co-Pilot";
        if (placementDescriptionLabel != null)
            placementDescriptionLabel.text = manual
                ? "Take control.\nArrange the music your way."
                : "Catch the notes.\nLet the cosmos handle the rest.";
    }

    void Start()
    {
        _hangar = FindAnyObjectByType<Hangar>();
        if (_hangar?.planeSelection != null)
        {
            transform.SetParent(_hangar.planeSelection.transform);
            transform.localScale = Vector3.one;
        }

        RefreshPlacementLabel();
        ApplyStageVisuals();
        if (_hangar)
        {
            AssignFirstAvailablePlane();
            SetVehicleStats();
        }
        else
        {
            Debug.LogError("❌ Hangar not found.");
        }
    }

    private void AssignFirstAvailablePlane()
    {
        _chosenPlane = _hangar.FirstAvailablePlane();

        if (_chosenPlane == null)
        {
            Debug.LogWarning("No available planes to assign.");
            return;
        }

        SetCurrentShipName(_chosenPlane.name.Replace("(Clone)", "").Trim());
        ApplyVisuals();
        _hangar.MarkPlaneInUse(_chosenPlane, true);
    }
    private void SetVehicleStats()
    {
        if (_chosenPlane && _chosenPlane.TryGetComponent(out Vehicle vehicle))
        {
            SetStats(vehicle);
        }
    }
    private void SetStats(Vehicle vehicle)
    {
        if (statDisplay == null) return;

        ShipMusicalProfile profile = vehicle.profile;
        float speed, agility, power, fuel;

        if (profile != null)
        {
            speed   = profile.arcadeMaxSpeed / 20f;
            agility = profile.directionalAuthority01;
            power   = profile.plowChipAmount
                      * (profile.plowHalfWidthCells * 2 + 1)
                      * (1f + profile.carveResistanceBypass01)
                      / 15f;
            fuel    = (profile.capacity / profile.burnRate) / 700f;

            if (modelNameLabel != null)
                modelNameLabel.text = profile.shipName;
        }
        else
        {
            speed   = vehicle.arcadeMaxSpeed / 20f;
            agility = 0.5f;
            power   = 0.5f;
            fuel    = vehicle.capacity / 50f;

            if (modelNameLabel != null)
                modelNameLabel.text = _currentShipName;
        }

        statDisplay.SetStats(
            Mathf.Clamp01(speed),
            Mathf.Clamp01(agility),
            Mathf.Clamp01(power),
            Mathf.Clamp01(fuel)
        );
    }

    private void ApplyVisuals()
    {
        SpriteRenderer sr = _chosenPlane.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            planeIcon.sprite = sr.sprite;
            if (vehicleIcon != null) vehicleIcon.sprite = sr.sprite;
        }
    }
    public void Confirm()
    {
        // The dimmed vehicle icon is the "confirmed" display; it lives under the Vehicle parent,
        // which is hidden during the groove stage, so bring it back and hide the groove UI.
        if (vehicleStage != null) vehicleStage.SetActive(true);
        if (grooveStyleStage != null) grooveStyleStage.SetActive(false);
        controlsAndStats.SetActive(false);
        border.enabled = false;

        // Dim (not hide) the icon so the player can see they're still in. A CanvasGroup rather than
        // tinting planeIcon.color: LocalPlayer.SetColor reads that color right after Confirm().
        var holder = planeIcon.transform.parent.gameObject;
        var group = holder.GetComponent<CanvasGroup>();
        if (group == null) group = holder.AddComponent<CanvasGroup>();
        group.alpha = confirmedAlpha;
    }

    /// <summary>Inverse of Confirm(): returns to the groove-style stage with the full (undimmed) selection UI.</summary>
    public void Unconfirm()
    {
        Stage = SelectStage.GrooveStyle;
        ApplyStageVisuals();
        controlsAndStats.SetActive(true);
        border.enabled = true;

        var holder = planeIcon.transform.parent.gameObject;
        var group = holder.GetComponent<CanvasGroup>();
        if (group != null) group.alpha = 1f;
    }

    public void NextVehicle()
    {
        if (_chosenPlane != null)
            _hangar.MarkPlaneInUse(_chosenPlane, false);

        int currentIndex = System.Array.IndexOf(_hangar.planes, _chosenPlane);
        int nextIndex = _hangar.NextAvailablePlane(currentIndex);
        _chosenPlane = _hangar.planes[nextIndex];
        SetCurrentShipName(_chosenPlane.name.Replace("(Clone)", "").Trim());
        ApplyVisuals();
        SetVehicleStats();
        _hangar.MarkPlaneInUse(_chosenPlane, true);
    }
    public void PreviousVehicle()
    {
        if (_chosenPlane != null)
            _hangar.MarkPlaneInUse(_chosenPlane, false);

        int currentIndex = System.Array.IndexOf(_hangar.planes, _chosenPlane);
        int prevIndex = _hangar.PreviousAvailableVehicle(currentIndex);
        _chosenPlane = _hangar.planes[prevIndex];
        SetCurrentShipName(_chosenPlane.name.Replace("(Clone)", "").Trim());

        ApplyVisuals();
        SetVehicleStats();
        _hangar.MarkPlaneInUse(_chosenPlane, true);
    }
    public GameObject GetChosenPlane()
    {
        return _chosenPlane;
    }
    public void ReleasePlane()
    {
        if (_chosenPlane != null) _hangar?.MarkPlaneInUse(_chosenPlane, false);
    }
    public string GetCurrentShipName()
    {
        return _currentShipName; // or however you're storing it internally
    }
    private void SetCurrentShipName(string name)
    {
        _currentShipName = name;
    }


}
