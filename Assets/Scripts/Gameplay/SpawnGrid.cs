using UnityEngine;
using System.Collections.Generic;
using System.Collections;

public class SpawnGrid : MonoBehaviour
{
    public int gridWidth = 8;
    public int gridHeight = 12;
    private GridCell[,] gridCells;
    public float cellSize = 1f; // Adjust to match the world space size

    
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

    public bool IsCellAvailable(int x, int y)
    {
        if (gridCells == null)
        {
            Debug.LogError("SpawnGrid: GridCell is Null!");
            return false;
        }
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.Log($"Cell check out of bounds: {x}, {y}");
            return false;
        }

        if (gridCells[x, y] == null)
        {
            Debug.Log($"Cell {x}, {y} is null.");
            return false;
        }

        return !gridCells[x, y].isOccupied; // ✅ Correctly check occupancy
    }


    public void OccupyCell(int x, int y, GridObjectType type, NodeType nodeType = NodeType.Standard)
    {
        // ✅ Ensure indices are within grid boundaries
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogError($"OccupyCell: Attempted to occupy cell out of bounds! (x:{x}, y:{y}), Grid Size: {gridWidth}x{gridHeight}");
            return;
        }

        if (gridCells[x, y] == null)
        {
            Debug.LogError($"OccupyCell: gridCells[{x}, {y}] is NULL!");
            return;
        }

        gridCells[x, y].isOccupied = true;
        gridCells[x, y].objectType = type;
        gridCells[x, y].nodeType = nodeType;
    }

    public void FreeCell(int x, int y)
    {
        // Ensure the coordinates are within valid bounds
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogWarning($"Trying to free a cell out of bounds: {x}, {y}");
            return;
        }

        if (gridCells[x, y] == null)
        {
            Debug.LogWarning($"Trying to free a null cell at: {x}, {y}");
            return;
        }

        // Mark the cell as available
        gridCells[x, y].isOccupied = false;
        gridCells[x, y].objectType = GridObjectType.Empty;
        gridCells[x, y].nodeType = null;

        Debug.Log($"Cell {x}, {y} successfully freed.");
    }
    public void ClearAll()
    {
        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                gridCells[x, y].isOccupied = false;
                gridCells[x, y].objectType = GridObjectType.Empty;

                // Optionally reset other cell state like visual effects, colors, or behavior
                ResetCellBehavior(x, y); // Call this if you already use it elsewhere
            }
        }

        Debug.Log("[SpawnGrid] Grid cleared.");
    }

    public void ResetCellBehavior(int x, int y)
    {
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
        {
            Debug.LogError($"ResetCellBehavior: Out of bounds ({x}, {y})");
            return;
        }
        gridCells[x, y].objectType = GridObjectType.Empty;
        gridCells[x, y].isOccupied = false;
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
    
    public Vector2Int GetRandomAvailableCell()
    {
        List<Vector2Int> availableCells = new List<Vector2Int>();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                if (!gridCells[x, y].isOccupied)
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
    
    public Vector2Int GetRandomAvailableCell(NoteBehavior noteBehavior)
    {
        List<Vector2Int> availableCells = new List<Vector2Int>();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                if (IsCellAvailable(x, y))
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
    public NodeType? nodeType = null;
}

public enum GridObjectType
{
    Note,
    Node,
    Empty,
    DrumCollectable
}