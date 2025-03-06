using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Explosion : MonoBehaviour {
    // Use this for initialization

    void Start()
    {
        Camera.main.gameObject.GetComponent<Kino.AnalogGlitch>().colorDrift = .2f;
        //GLITCH RUNS ON DESTROY, THIS CUTS OFF LONGER EXPLOSIONS
        Invoke("ResetGlitch", .1f);

    }
    void Update()
    {
        if(GetComponent<ParticleSystem>())
        {
            if(!GetComponent<ParticleSystem>().isPlaying)
            {
                ResetGlitch();
                Destroy(this.gameObject);
            }
        }    
    }

    void ResetGlitch()
    {
        Camera.main.gameObject.GetComponent<Kino.AnalogGlitch>().scanLineJitter = 0f;
        Camera.main.gameObject.GetComponent<Kino.AnalogGlitch>().colorDrift = 0f;
        Camera.main.gameObject.GetComponent<Kino.AnalogGlitch>().verticalJump = 0f;
    }

}
