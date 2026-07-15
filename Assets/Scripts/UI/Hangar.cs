using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

public class Hangar : MonoBehaviour
{
    public static Hangar Instance { get; private set; }

    [Header("Plane Setup")]
    public GameObject[] planes;
    public GameObject planeSelection;

    private Dictionary<GameObject, bool> planeInUseMap = new();
    private readonly HashSet<GameObject> _permanentlyUsed = new();

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // The scene reloaded — refresh the scene-specific planeSelection reference on
            // the persisting instance before discarding this duplicate.
            if (planeSelection != null)
                Instance.planeSelection = planeSelection;
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        foreach (GameObject plane in planes)
            planeInUseMap[plane] = false;
    }

    public void ResetForNewSession()
    {
        _permanentlyUsed.Clear();
        foreach (var key in planeInUseMap.Keys.ToList())
            planeInUseMap[key] = false;
    }

    public void MarkVehiclePermanentlyUsed(GameObject prefab)
    {
        if (prefab == null) return;
        _permanentlyUsed.Add(prefab);
        if (planeInUseMap.ContainsKey(prefab))
            planeInUseMap[prefab] = true;
    }

    public bool HasAvailableVehicles()
    {
        foreach (var plane in planes)
            if (!_permanentlyUsed.Contains(plane)) return true;
        return false;
    }

    public GameObject FirstAvailablePlane()
    {
        foreach (var kvp in planeInUseMap)
        {
            if (!kvp.Value && !_permanentlyUsed.Contains(kvp.Key))
            {
                planeInUseMap[kvp.Key] = true;
                return kvp.Key;
            }
        }
        return null;
    }

    public int NextAvailablePlane(int currentIndex)
    {
        // Skip in-use and permanently-used planes while cycling forward
        for (int i = 1; i < planes.Length; i++)
        {
            int index = (currentIndex + i) % planes.Length;
            if (!planeInUseMap[planes[index]] && !_permanentlyUsed.Contains(planes[index]))
                return index;
        }
        return currentIndex;
    }

    public int PreviousAvailableVehicle(int currentIndex)
    {
        // Skip in-use and permanently-used planes while cycling backward
        for (int i = 1; i < planes.Length; i++)
        {
            int index = (currentIndex - i + planes.Length) % planes.Length;
            if (!planeInUseMap[planes[index]] && !_permanentlyUsed.Contains(planes[index]))
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
            float cap = (vehicle != null && vehicle.profile != null) ? vehicle.profile.capacity : (vehicle != null ? vehicle.capacity : 0f);
            if (cap > max) max = cap;
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
    
}
