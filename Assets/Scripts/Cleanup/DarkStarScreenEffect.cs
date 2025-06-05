using UnityEngine;

public class DarkStarScreenEffect : MonoBehaviour
{
    public Material screenEffectMaterial;
    public float effectDuration = 1.5f;
    public float intensity;
    private Material runtimeMat;
    private float timer;
    private bool active = false;

    void Start()
    {
        runtimeMat = new Material(screenEffectMaterial);
    }

    void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (active)
        {
            timer += Time.deltaTime;
            float t = timer / effectDuration;
            runtimeMat.SetFloat("_Intensity", Mathf.Lerp(1f, 0f, t));

            Graphics.Blit(src, dest, runtimeMat);

            if (t >= 1f)
                active = false;
        }
        else
        {
            Graphics.Blit(src, dest);
        }
    }

    public void Trigger(float _intensity = 1f, float duration = 1.5f)
    {
        this.effectDuration = duration;
        this.intensity = _intensity;
        timer = 0f;
        active = true;
    }
}
