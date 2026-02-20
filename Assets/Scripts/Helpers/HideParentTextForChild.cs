using UnityEngine;

public class HideParentTextForChild : MonoBehaviour
{
    public GameObject childToWatch;

    private bool changeToStatus = true;
    // Start is called once before the first execution of Update after the MonoBehaviour is created

    // Update is called once per frame
    void Update()
    {
        if (childToWatch.transform.childCount > 0)
        {
     Debug.Log($"[TUTORIAL] Hiding Parent Text");
            gameObject.SetActive(false);
        }
        else
        {
            Debug.Log($"[TUTORIAL] child count: {childToWatch.transform.childCount}");
        }
    }
}
