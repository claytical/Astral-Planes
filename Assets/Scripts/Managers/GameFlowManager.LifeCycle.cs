using System.Collections.Generic;
using UnityEngine;

public partial class GameFlowManager
{
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            vehicles = new List<Vehicle>();
            return;
        }

        Destroy(gameObject);
    }
}