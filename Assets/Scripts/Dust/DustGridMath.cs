using System.Collections.Generic;
using UnityEngine;

internal static class DustGridMath
{
    internal static void FillDisk(HashSet<Vector2Int> result, Vector2Int center, int r, int w, int h)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            if (dx * dx + dy * dy > r * r) continue;
            int x = center.x + dx; int y = center.y + dy;
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            result.Add(new Vector2Int(x, y));
        }
    }
}
