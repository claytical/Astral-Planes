using System.Collections;
using UnityEngine;

public class Obstacle : MonoBehaviour
{
    // The current grid cell (using integer coordinates)
    public Vector2Int gridPosition;
    // Time between moves (should match your drum beat)
    // Custom grid parameters
    public Vector2 gridOrigin;  // e.g. (screenMinX + xOffset, obstacleInitialY)
    public Vector2 cellSize;    // e.g. (stepWidth, desiredYStep)
}
