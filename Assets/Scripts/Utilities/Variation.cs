using System;
using UnityEngine;

[Serializable] public struct Weighted<T> { public T item; [Range(0,1f)] public float weight; }

[Serializable]
public class VariationProfile {
    [Header("Pitch / chord")]
    [Range(0,1f)] public float extensionBias = 0.35f;

    [Header("Register / range")]
    [Range(0,1f)] public float octaveMoveProb = 0.10f;

    [Header("Rhythm / timing")]
    [Range(0,1f)] public float restProb = 0.10f;
    [Range(0.5f,1.5f)] public float durJitter = 1.0f;

}