using System;
using UnityEngine;

[Serializable] public struct Weighted<T> { public T item; [Range(0,1f)] public float weight; }

[Serializable]
public class VariationProfile {
    [Header("Pitch / chord")]
    [Range(0,1f)] public float extensionBias = 0.35f;
    [Range(0,1f)] public float passingToneProb = 0.20f;
    [Range(0,1f)] public float neighborOrnProb = 0.15f;

    [Header("Register / range")]
    public int minMidi = 36;
    public int maxMidi = 84;
    [Range(0,1f)] public float leapProb = 0.15f;
    [Range(0,1f)] public float octaveMoveProb = 0.10f;

    [Header("Rhythm / timing")]
    [Range(0,1f)] public float restProb = 0.10f;
    [Range(0f,0.06f)] public float humanizeMs = 0.02f;
    [Range(0.5f,1.5f)] public float durJitter = 1.0f;

    [Header("Loop mutation (per loop)")]
    [Range(0,1f)] public float mutatePitch = 0.08f;
    [Range(0,1f)] public float mutateVelocity = 0.12f;
    [Range(0,1f)] public float mutateDuration = 0.10f;
}