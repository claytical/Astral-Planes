using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Explosion : MonoBehaviour {
    private ParticleSystem _ps;

    void Awake() { _ps = GetComponent<ParticleSystem>(); }

    void Update()
    {
        if (_ps != null && !_ps.isPlaying)
            Destroy(gameObject);
    }
}
