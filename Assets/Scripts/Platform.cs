using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidiPlayerTK;

[System.Serializable]
public enum PlatformType { Static, Blink, Scale, Move}
public class Platform : MonoBehaviour
{

    public PlatformType type;
    public int triggerNote = -1;
    public int triggerTrack = -1;

    public GameObject platform;  // This should be the object with the SpriteRenderer
    public bool indestructable = true;  // The indestructable property is reintroduced
    public RigidbodyConstraints2D constraints;

    public float scaleSpeed = 0.1f;
    [Range(0.1f, 5f)]
    public float timeToAppear = 0.1f;

    [Range(0.1f, 1f)]
    public float fadeInDuration = 0.5f;
    

    void OnDestroy()
    {
        // Clean up any references or stop any running coroutines if necessary
        StopAllCoroutines();
    }

}
