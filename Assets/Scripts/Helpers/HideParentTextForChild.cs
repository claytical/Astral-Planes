using UnityEngine;

public class HideParentTextForChild : MonoBehaviour
{
    public GameObject childToWatch;

    private bool changeToStatus = true;
    void Update()
    {
        if (childToWatch.transform.childCount > 0)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TUTORIAL] Hiding Parent Text");
            gameObject.SetActive(false);
        }
    }
}
