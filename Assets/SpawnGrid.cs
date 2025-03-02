using UnityEngine;
using System.Collections.Generic;

public class SpawnGrid
{
    private int gridWidth;
    private int gridHeight;
    private GridCell[,] gridCells;

    public SpawnGrid(int width, int height)
    {
        gridWidth = width;
        gridHeight = height;
        gridCells = new GridCell[gridWidth, gridHeight];

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                gridCells[x, y] = new GridCell();
            }
        }
    }

    public bool IsCellAvailable(int x, int y)
    {
        return !gridCells[x, y].isOccupied;
    }

    public void OccupyCell(int x, int y, GridObjectType type)
    {
        gridCells[x, y].isOccupied = true;
        gridCells[x, y].objectType = type;
    }

    public void FreeCell(int x, int y)
    {
        gridCells[x, y].isOccupied = false;
    }

    public Vector2Int GetRandomAvailableCell()
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

        if (availableCells.Count > 0)
        {
            return availableCells[Random.Range(0, availableCells.Count)];
        }

        return new Vector2Int(-1, -1); // No available cells
    }
}

public class GridCell
{
    public bool isOccupied = false;
    public GridObjectType objectType;
}

public enum GridObjectType
{
    Note,
    Obstacle,
    DrumCollectable
}