using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface IMotifCoralAnimationController
{
    IEnumerator RunGrowth(float durationSec, AnimationCurve curve, Func<float> deltaTime, Action<float> onProgress);
}

public sealed class MotifCoralAnimationController : IMotifCoralAnimationController
{
    public IEnumerator RunGrowth(float durationSec, AnimationCurve curve, Func<float> deltaTime, Action<float> onProgress)
    {
        float dur=Mathf.Max(0.05f,durationSec); float e=0f;
        while(e<dur){ e+=deltaTime(); float u=Mathf.Clamp01(e/dur); onProgress(curve!=null?curve.Evaluate(u):u); yield return null; }
        onProgress(1f);
    }
}
