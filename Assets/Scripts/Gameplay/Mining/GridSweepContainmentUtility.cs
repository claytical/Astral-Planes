using UnityEngine;

public static class GridSweepContainmentUtility
{
    public struct SweepHit
    {
        public bool hit;
        public Vector2Int blockedCell;
        public Vector2 normal;
        public Vector2 impactPoint;
        public float travelT;
    }

    // Wrapping policy: all reads route through DrumTrack.WorldToGridPosition / WrapGridCell + HasDustAt,
    // so toroidal tracks wrap and non-toroidal tracks clamp consistently.
    public static SweepHit FindFirstBlockedCrossing(DrumTrack drumTrack, Vector2 fromPos, Vector2 toPos)
    {
        SweepHit result = default;
        Vector2 delta = toPos - fromPos;
        float maxAxis = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
        int steps = Mathf.Clamp(Mathf.CeilToInt(maxAxis * 8f), 1, 128);

        Vector2Int prevCell = drumTrack.WorldToGridPosition(fromPos);
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            Vector2 sample = Vector2.Lerp(fromPos, toPos, t);
            Vector2Int sampleCell = drumTrack.WorldToGridPosition(sample);

            int stepX = sampleCell.x - prevCell.x;
            int stepY = sampleCell.y - prevCell.y;
            if (stepX != 0) stepX = stepX > 0 ? 1 : -1;
            if (stepY != 0) stepY = stepY > 0 ? 1 : -1;

            if (stepX != 0 && stepY != 0)
            {
                Vector2Int sideX = drumTrack.WrapGridCell(prevCell + new Vector2Int(stepX, 0));
                Vector2Int sideY = drumTrack.WrapGridCell(prevCell + new Vector2Int(0, stepY));
                bool blockX = drumTrack.HasDustAt(sideX);
                bool blockY = drumTrack.HasDustAt(sideY);

                // Reject corner-cut only when both orthogonal neighbors are blocked in the same sweep step.
                if (blockX && blockY)
                {
                    result.hit = true;
                    result.blockedCell = sampleCell;
                    result.impactPoint = sample;
                    result.travelT = t;
                    Vector2 n = new Vector2(-stepX, -stepY);
                    result.normal = n.sqrMagnitude > 0.0001f ? n.normalized : Vector2.zero;
                    return result;
                }
            }

            if (drumTrack.HasDustAt(sampleCell))
            {
                result.hit = true;
                result.blockedCell = sampleCell;
                result.impactPoint = sample;
                result.travelT = t;

                if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                    result.normal = new Vector2(delta.x > 0f ? -1f : 1f, 0f);
                else
                    result.normal = new Vector2(0f, delta.y > 0f ? -1f : 1f);

                return result;
            }

            prevCell = sampleCell;
        }

        return result;
    }
}
