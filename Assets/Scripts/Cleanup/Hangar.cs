using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class Hangar : MonoBehaviour
{
    [Header("Plane Setup")]
    public GameObject[] planes;
    public GameObject setupScreen;

    private Dictionary<GameObject, bool> planeInUseMap = new();

    void Start()
    {
        foreach (GameObject plane in planes)
        {
            planeInUseMap[plane] = false;
        }
    }

    public void OnJoin(PlayerInput playerInput)
    {
        // Reserved for player join logic if needed
    }

    public GameObject FirstAvailablePlane()
    {
        foreach (var kvp in planeInUseMap)
        {
            if (!kvp.Value)
            {
                planeInUseMap[kvp.Key] = true;
                return kvp.Key;
            }
        }
        return null;
    }

    public int NextAvailablePlane(int currentIndex)
    {
        // Skip in-use planes while cycling forward
        for (int i = 1; i < planes.Length; i++)
        {
            int index = (currentIndex + i) % planes.Length;
            if (!planeInUseMap[planes[index]])
                return index;
        }
        return currentIndex;
    }

    public int PreviousAvailableVehicle(int currentIndex)
    {
        // Skip in-use planes while cycling backward
        for (int i = 1; i < planes.Length; i++)
        {
            int index = (currentIndex - i + planes.Length) % planes.Length;
            if (!planeInUseMap[planes[index]])
                return index;
        }
        return currentIndex;
    }

    public float GetMaxCapacity()
    {
        float max = 0f;
        foreach (GameObject plane in planes)
        {
            Vehicle vehicle = plane.GetComponent<Vehicle>();
            if (vehicle != null && vehicle.capacity > max)
            {
                max = vehicle.capacity;
            }
        }
        return max;
    }

    public void MarkPlaneInUse(GameObject plane, bool inUse)
    {
        if (planeInUseMap.ContainsKey(plane))
        {
            planeInUseMap[plane] = inUse;
        }
    }

    public bool IsPlaneInUse(GameObject plane)
    {
        return planeInUseMap.ContainsKey(plane) && planeInUseMap[plane];
    }
}
