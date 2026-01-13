using UnityEngine;
using System.Collections.Generic;
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
public class SpawnGrid : MonoBehaviour
{
    public int gridWidth = 8;
    public int gridHeight = 12;
    public GridCell[,] GridCells;
    public float cellSize = 1f; // Adjust to match the world space size
    private DrumTrack _drums;
    
    private void Awake()
    {
        if (_drums == null)
        {
            // Prefer the active drum track from the GameFlowManager
            var gfm = GameFlowManager.Instance;
            if (gfm != null && gfm.activeDrumTrack != null)
                _drums = gfm.activeDrumTrack;
            else
                _drums = FindObjectOfType<DrumTrack>();
        }

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
        // Only draw the runtime-accurate grid when the game is running,
        // because DrumTrack / dust sizing are only valid then.
        if (!Application.isPlaying)
            return;


        if (_drums == null)
            return;

        // Use the authoritative grid dimensions from DrumTrack / SpawnGrid
        
        if (gridWidth <= 0 || gridHeight <= 0) return;

        float runtimeCellSize = _drums.GetCellWorldSize();

        // Optionally sync our local cellSize field so any other code using it
        // isn't wildly wrong during play.
        cellSize = runtimeCellSize;

        // Draw per-cell boxes at the *runtime* positions
        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                var gp    = new Vector2Int(x, y);
                var world = _drums.GridToWorldPosition(gp);

                bool occupied = GridCells != null &&
                                GridCells[x, y] != null &&
                                GridCells[x, y].IsOccupied;

                Gizmos.color = occupied
                    ? Color.red                         // occupied cell
                    : new Color(1f, 1f, 1f, 0.25f);     // empty grid cell

                Gizmos.DrawWireCube(world,
                    Vector3.one * runtimeCellSize * 0.9f);
            }
        }
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
    public void ResizeGrid(int newWidth, int newHeight) {
        newWidth  = Mathf.Max(1, newWidth); 
        newHeight = Mathf.Max(1, newHeight);
        // No-op if already correct and initialized
        if (GridCells != null && GridCells.GetLength(0) == newWidth && GridCells.GetLength(1) == newHeight) {
            gridWidth = newWidth; 
            gridHeight = newHeight; 
            return;
        }
        
        gridWidth  = newWidth; 
        gridHeight = newHeight;  
        InitializeGrid();
        
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
    }    
}

