using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UIElements;
using Image = UnityEngine.UI.Image;

public class StarProgressUI : MonoBehaviour
{
    [Header("Shard Settings")]
    public GameObject shardPrefab;
    public int totalShards = 7; // default full cycle
    public float radius = 50f;
    public float startRotationOffset = 90f;

    [Header("Colors")]
    public Color lockedColor = new Color(1f, 1f, 1f, 0.05f);
    public Color debugColor = new Color(1f, 0f, 0f, 0.6f);
    public Color fixColor = new Color(1f, 1f, 1f, .9f);

    private List<Image> shardImages = new List<Image>();

    public enum ShardState
    {
        Locked,
        Debug,
        Fixed
    }

    public void Initialize(int shardCount)
    {
        totalShards = shardCount;
        foreach (Transform child in transform)
        {
            Destroy(child.gameObject);
        }

        shardImages.Clear();

        for (int i = 0; i < totalShards; i++)
        {
            float angle = startRotationOffset + (360f / totalShards) * i;
            Vector2 pos = new Vector2(
                Mathf.Cos(angle * Mathf.Deg2Rad),
                Mathf.Sin(angle * Mathf.Deg2Rad)) * radius;

            GameObject shard = Instantiate(shardPrefab, transform);
            RotateConstant rc = shard.GetComponent<RotateConstant>();
            if (rc != null)
            {
                rc.shardIndex = i;
                rc.totalShards = shardCount;
                rc.baseSpeed = 20f + i * 2f; // stagger speed
                rc.ApplyRotationSettings();
            }

            shard.GetComponent<RectTransform>().anchoredPosition = pos;
            shard.GetComponent<RectTransform>().localRotation = Quaternion.Euler(0, 0, angle + 45f); // slight isometric tilt

            Image img = shard.GetComponent<Image>();
            img.color = lockedColor;
            shardImages.Add(img);
        }
    }

    public void SetShardState(int index, ShardState state)
    {
        if (index < 0 || index >= shardImages.Count) return;

        float angle = startRotationOffset + (360f / totalShards) * index;
        RectTransform rect = shardImages[index].rectTransform;
        rect.localRotation = Quaternion.Euler(0, 0, angle + 45f);

        RotateConstant rc = shardImages[index].GetComponent<RotateConstant>();
        if (rc != null)
        {
            rc.shardIndex = index;
            rc.totalShards = totalShards;

            // Always rotate
            rc.rotationMode = RotationMode.Uniform;

            // Flip direction based on state
            switch (state)
            {
                case ShardState.Locked:
                    rc.baseSpeed = Mathf.Abs(rc.baseSpeed); // clockwise
                    shardImages[index].color = lockedColor;
                    break;

                case ShardState.Debug:
                    rc.baseSpeed = -Mathf.Abs(rc.baseSpeed); // counter-clockwise
                    shardImages[index].color = debugColor;
                    break;

                case ShardState.Fixed:
                    rc.baseSpeed = Mathf.Abs(rc.baseSpeed); // clockwise
                    shardImages[index].color = fixColor;
                    break;
            }

            rc.ApplyRotationSettings(); // must reapply new speed/direction
        }
    }

}