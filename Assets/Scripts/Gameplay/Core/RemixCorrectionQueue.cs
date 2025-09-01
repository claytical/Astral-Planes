using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RemixCorrectionQueue : MonoBehaviour
{
    public static RemixCorrectionQueue Instance { get; private set; }

    [Tooltip("Seconds between each correction placement onto the ribbon.")]
    public float cadenceSeconds = 0.08f;

    private readonly Queue<System.Action> queue = new();
    private bool running;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public void Enqueue(System.Action action)
    {
        queue.Enqueue(action);
        if (!running) StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        running = true;
        while (queue.Count > 0)
        {
            var a = queue.Dequeue();
            a?.Invoke();
            yield return new WaitForSeconds(cadenceSeconds);
        }
        running = false;
    }
}