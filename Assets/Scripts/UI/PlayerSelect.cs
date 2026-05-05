using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerSelect : MonoBehaviour
{
    public Image planeIcon;
    public Fuel fuel;
    public GameObject controlsAndStats;
    public GameObject tutorialControls;
    public Image border;

    private int _selectedColor = 0;
    private Hangar _hangar;
    private GameObject _chosenPlane;
    private string _currentShipName;

    void Start()
    {
        _hangar = FindAnyObjectByType<Hangar>();
        transform.SetParent(_hangar.planeSelection.transform);
        transform.localScale = Vector3.one;

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
        SetCurrentShipName(_chosenPlane.name.Replace("(Clone)", "").Trim());

        if (_chosenPlane == null)
        {
            Debug.LogWarning("No available planes to assign.");
            return;
        }

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
        if (fuel == null)
        {
            return;
        }

        ShipMusicalProfile profile = vehicle.profile;

        const float maxCapacityForUI = 250f;
        const float minBurnRateForUI = 0.25f;
        const float maxBurnRateForUI = 1f;
        const float speedTopEndForUI = 20f;
        const float boostTopEndForUI = 280f;

        float capacity = profile != null ? profile.capacity : vehicle.capacity;
        float burnRate = profile != null ? profile.burnRate : minBurnRateForUI;
        float maxSpeed = profile != null ? profile.arcadeMaxSpeed : vehicle.arcadeMaxSpeed;
        float boostAccel = profile != null ? profile.arcadeBoostAccel : 0f;

        float capacityRatio = capacity / maxCapacityForUI;
        float fuelEfficiencyRatio = Mathf.InverseLerp(minBurnRateForUI, maxBurnRateForUI, burnRate);
        float speedRatio = maxSpeed / speedTopEndForUI;
        float boostRatio = boostAccel / boostTopEndForUI;

        fuel.UpdateProfileComparisonUI(capacityRatio, fuelEfficiencyRatio, speedRatio, boostRatio);
    }

    private void ApplyVisuals()
    {
        SpriteRenderer sr = _chosenPlane.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            planeIcon.sprite = sr.sprite;
        }
    }
    public void Confirm()
    {
        controlsAndStats.SetActive(false);
        planeIcon.transform.parent.gameObject.SetActive(false);
        border.enabled = false;
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
    public string GetCurrentShipName()
    {
        return _currentShipName; // or however you're storing it internally
    }
    private void SetCurrentShipName(string name)
    {
        _currentShipName = name;
    }


}
