using UnityEngine;
using System.Collections.Generic;

public class SpawnGrid : MonoBehaviour
{
    public int gridWidth = 8;
    public int gridHeight = 12;
    private GridCell[,] GridCells;
    public float cellSize = 1f; // Adjust to match the world space size

    
    private void Awake()
    {
        if (GridCells == null || GridCells.Length == 0)
        {
            InitializeGrid();
        }
    }

    private void InitializeGrid()
    {
        GridCells = new GridCell[gridWidth, gridHeight];

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                GridCells[x, y] = new GridCell(); // ✅ Ensure every cell is initialized
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
        if (GridCells != null)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                for (int y = 0; y < gridHeight; y++)
                {
                    if (GridCells[x, y].IsOccupied)
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
                if (GridCells[x, y].IsOccupied)
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
    public Vector2Int GetRandomAvailableCell()
    {
        List<Vector2Int> availableCells = new List<Vector2Int>();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                if (!GridCells[x, y].IsOccupied)
                {
                    availableCells.Add(new Vector2Int(x, y));
                }
            }
        }

        if (availableCells.Count == 0)
        {
            return new Vector2Int(-1, -1);
        }

        return availableCells[Random.Range(0, availableCells.Count)];
    }
    public bool IsCellAvailable(int x, int y)
    {
        if (GridCells == null)
        {
            Debug.LogError("SpawnGrid: GridCell is Null!");
            return false;
        }
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
//            Debug.Log($"Cell check out of bounds: {x}, {y}");
            return false;
        }

        if (GridCells[x, y] == null)
        {
  //          Debug.Log($"Cell {x}, {y} is null.");
            return false;
        }

        return !GridCells[x, y].IsOccupied; // ✅ Correctly check occupancy
    }
    public void OccupyCell(int x, int y, GridObjectType type)
    {
        // ✅ Ensure indices are within grid boundaries
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogError($"OccupyCell: Attempted to occupy cell out of bounds! (x:{x}, y:{y}), Grid Size: {gridWidth}x{gridHeight}");
            return;
        }

        if (GridCells[x, y] == null)
        {
            Debug.LogError($"OccupyCell: gridCells[{x}, {y}] is NULL!");
            return;
        }

        GridCells[x, y].IsOccupied = true;
        GridCells[x, y].ObjectType = type;
    }
    public void FreeCell(int x, int y)
    {
        // Ensure the coordinates are within valid bounds
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogWarning($"Trying to free a cell out of bounds: {x}, {y}");
            return;
        }

        if (GridCells[x, y] == null)
        {
            Debug.LogWarning($"Trying to free a null cell at: {x}, {y}");
            return;
        }

        // Mark the cell as available
        GridCells[x, y].IsOccupied = false;
        GridCells[x, y].ObjectType = GridObjectType.Empty;

    //    Debug.Log($"Cell {x}, {y} successfully freed.");
    } 
    public void ResetCellBehavior(int x, int y)
    {
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogError($"ResetCellBehavior: Out of bounds ({x}, {y})");
            return;
        }
        GridCells[x, y].ObjectType = GridObjectType.Empty;
        GridCells[x, y].IsOccupied = false;
    }    public void ClearAll()
    {
        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                GridCells[x, y].IsOccupied = false;
                GridCells[x, y].ObjectType = GridObjectType.Empty;

                // Optionally reset other cell state like visual effects, colors, or behavior
                ResetCellBehavior(x, y); // Call this if you already use it elsewhere
            }
        }

        Debug.Log("[SpawnGrid] Grid cleared.");
    }

}

public class GridCell {
    public bool IsOccupied;
    public GridObjectType ObjectType = GridObjectType.Empty;
}

public enum GridObjectType {
    Note,
    Node,
    Empty,
    Dust
}