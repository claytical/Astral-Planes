using UnityEngine;
using System.Collections.Generic;

public class SpawnGrid : MonoBehaviour
{
    public int gridWidth = 8;
    public int gridHeight = 5;
    private GridCell[,] gridCells;
    public float cellSize = 2f; // Adjust to match the world space size

    
    private void Awake()
    {
        if (gridCells == null || gridCells.Length == 0)
        {
            InitializeGrid();
        }
    }

    private void InitializeGrid()
    {
        gridCells = new GridCell[gridWidth, gridHeight];

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                gridCells[x, y] = new GridCell(); // ✅ Ensure every cell is initialized
            }
        }
    }

    private void OnDrawGizmos()
    {
        // Draw the grid structure
        Gizmos.color = Color.white;
        for (int x = 0; x <= gridWidth; x++)
        {
            Gizmos.DrawLine(new Vector3(x * cellSize - (gridWidth * cellSize / 2), -gridHeight * cellSize / 2, 0),
                new Vector3(x * cellSize - (gridWidth * cellSize / 2), gridHeight * cellSize / 2, 0));
        }
        for (int y = 0; y <= gridHeight; y++)
        {
            Gizmos.DrawLine(new Vector3(-gridWidth * cellSize / 2, y * cellSize - (gridHeight * cellSize / 2), 0),
                new Vector3(gridWidth * cellSize / 2, y * cellSize - (gridHeight * cellSize / 2), 0));
        }

        // Draw occupied cells
        if (gridCells != null)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                for (int y = 0; y < gridHeight; y++)
                {
                    if (gridCells[x, y].isOccupied)
                    {
                        Gizmos.color = Color.red; // Highlight occupied cells
                        Vector3 cellCenter = new Vector3(x * cellSize - (gridWidth * cellSize / 2) + cellSize / 2,
                            y * cellSize - (gridHeight * cellSize / 2) + cellSize / 2, 0);
                        Gizmos.DrawCube(cellCenter, new Vector3(cellSize * 0.9f, cellSize * 0.9f, 0.1f));
                    }
                }
            }
        }
    }
    public void PrintGridDebug()
    {
        Debug.Log("---- Grid Debug Map ----");
        for (int y = gridHeight - 1; y >= 0; y--) // Print from top to bottom
        {
            string row = "";
            for (int x = 0; x < gridWidth; x++)
            {
                if (gridCells[x, y].isOccupied)
                {
                    row += "[X] "; // Mark occupied cells
                }
                else
                {
                    row += "[ ] "; // Mark free cells
                }
            }
            Debug.Log(row);
        }
        Debug.Log("---- End of Grid Debug ----");
    }

    public bool IsCellAvailable(int x, int y, NoteBehavior behavior)
    {
        // ✅ Ensure `gridCells` is initialized
        if (gridCells == null)
        {
            Debug.LogError("IsCellAvailable: gridCells is NULL!");
            return false;
        }

        // ✅ Ensure x and y are within valid bounds
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogError($"IsCellAvailable: Index out of bounds! (x:{x}, y:{y}) Grid Size: {gridWidth}x{gridHeight}");
            return false;
        }

        // ✅ Ensure cell is not null before accessing properties
        if (gridCells[x, y] == null)
        {
            Debug.LogError($"IsCellAvailable: gridCells[{x}, {y}] is NULL!");
            return false;
        }

        // ✅ Check if the cell is occupied
        if (gridCells[x, y].isOccupied)
        {
            return false;
        }

        // ✅ Ensure NoteBehavior is respected
        switch (behavior)
        {
            case NoteBehavior.Bass:
                return y <= gridHeight / 3; // Bass notes stay low
            case NoteBehavior.Lead:
                return y >= gridHeight / 2; // Lead notes stay high
            case NoteBehavior.Harmony:
                return y > gridHeight / 3 && y < (gridHeight * 2) / 3; // Harmony in the middle
            case NoteBehavior.Percussion:
                return y % 2 == 0; // Percussion is spaced evenly
            case NoteBehavior.Drone:
                return y == gridHeight / 2; // Drone notes stay in the center
            default:
                return true;
        }
    }
    public void OccupyCell(int x, int y, GridObjectType type, ObstacleType obstacleType = ObstacleType.Standard)
    {
        gridCells[x, y].isOccupied = true;
        gridCells[x, y].objectType = type;
        gridCells[x, y].obstacleType = obstacleType; // ✅ Store obstacle type
    }

    public void FreeCell(int x, int y)
    {
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogError($"FreeCell: Index out of bounds! (x:{x}, y:{y}) Grid Size: {gridWidth}x{gridHeight}");
            return;
        }

        if (gridCells[x, y].isOccupied)
        {
            gridCells[x, y].isOccupied = false;
            Debug.Log($"Cell ({x}, {y}) successfully freed.");
        }
        else
        {
            Debug.LogWarning($"Cell ({x}, {y}) was already free.");
        }
    }
    private bool IsCellValidForNoteBehavior(int x, int y, NoteBehavior behavior)
    {
        switch (behavior)
        {
            case NoteBehavior.Bass:
                return y <= gridHeight / 3; // Bass obstacles appear at the bottom
            case NoteBehavior.Lead:
                return y >= gridHeight / 2; // Lead obstacles appear at the top
            case NoteBehavior.Harmony:
                return y > gridHeight / 3 && y < (gridHeight * 2) / 3; // Harmony in the middle
            case NoteBehavior.Percussion:
                return y % 2 == 0; // Percussion obstacles are evenly spaced
            case NoteBehavior.Drone:
                return y == gridHeight / 2; // Drone obstacles stay in the center
        }
        return true;
    }
    
    private bool IsCellValidForDrumPattern(int x, int y, DrumLoopPattern pattern)
    {
        switch (pattern)
        {
            case DrumLoopPattern.Full:
                return y % 4 == 0; // Obstacles every 4th row
            case DrumLoopPattern.Breakbeat:
                return y % 2 != 0; // Offset placement for syncopation
            case DrumLoopPattern.SlowDown:
                return y < gridHeight / 2; // Lower-positioned obstacles
        }
        return true;
    }

    public void ResetCellBehavior(int x, int y)
    {
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogError($"ResetCellBehavior: Index out of bounds! (x:{x}, y:{y}) Grid Size: {gridWidth}x{gridHeight}");
            return;
        }

        // ✅ Reset the behavior of this cell
        if (gridCells[x, y] != null)
        {
            gridCells[x, y].isOccupied = false;
            gridCells[x, y].objectType = GridObjectType.Obstacle; // ✅ Clears any previous restrictions
            Debug.Log($"Reset cell behavior at ({x},{y}) - now available for any NoteBehavior.");
        }
    }

    public Vector2Int GetRandomAvailableCell(NoteBehavior noteBehavior)
    {
        List<Vector2Int> availableCells = new List<Vector2Int>();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                if (IsCellAvailable(x, y, noteBehavior))
                {
                    availableCells.Add(new Vector2Int(x, y));
                }
            }
        }

        Debug.Log($"Available Cells Count: {availableCells.Count}");

        if (availableCells.Count > 0)
        {
            return availableCells[Random.Range(0, availableCells.Count)];
        }

        Debug.LogWarning("No available cells found!");
        return new Vector2Int(-1, -1); // No available cells
    }



}

public class GridCell
{
    public bool isOccupied = false;
    public GridObjectType objectType;
    public ObstacleType? obstacleType = null;
}

public enum GridObjectType
{
    Note,
    Obstacle,
    DrumCollectable
}