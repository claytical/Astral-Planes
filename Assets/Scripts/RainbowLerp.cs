using UnityEngine;
using UnityEngine.UI;
public class RainbowLerp : MonoBehaviour
{
    [Range(0f, 5f)]
    public float colorCycleSpeed = 1f;

    private SpriteRenderer spriteRenderer;
    private Image image;
    private float hue;
    private bool isSprite = false;
    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            isSprite = true;
        }
        Image img = GetComponent<Image>();
        if (img != null)
        {
            image = img;
            isSprite = false;
        }
        // Optional: start at a random hue if you like
        hue = Random.value; 
    }

    private void Update()
    {
        // Increase hue value over time
        hue += colorCycleSpeed * Time.deltaTime;
        // Wrap hue around [0,1]
        if (hue > 1f) hue -= 1f;

        // Convert HSV to RGB. 
        // Full saturation (1f) & full value (1f) to get bright rainbow colors.
        Color rainbowColor = Color.HSVToRGB(hue, 1f, 1f);
        if (isSprite)
        {
            spriteRenderer.color = rainbowColor;
        }
        else
        {
            image.color = rainbowColor;
        }
    }
}