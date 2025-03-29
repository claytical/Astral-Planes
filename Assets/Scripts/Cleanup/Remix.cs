using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Remix : MonoBehaviour
{
    private RemixManager remixManager;
    public SpriteRenderer spriteRenderer;

    public void SetRemixManager(RemixManager rm)
    {
        remixManager = rm;
        SetColorBasedOnScript();

    }
    public void AdjustColors()
    {
        remixManager.AdjustColors(.01f, false);
    }

    private void SetColorBasedOnScript()
    {
        if (GetComponentInChildren<Collectable>())
        {
            spriteRenderer.color = remixManager.collectable;
        }
        else if (GetComponentInChildren<Hazard>())
        {
            spriteRenderer.color = remixManager.hazard;

        }
        
        else
        {
            Debug.LogWarning("No relevant script found on this GameObject to determine color.");
        }
    }
}
