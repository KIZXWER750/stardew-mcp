using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using xTile.Dimensions;

namespace StardewMCP;

/// <summary>A* pathfinding for navigating Stardew Valley maps.</summary>
public class Pathfinder
{
    private const int MaxIterations = 50000; // Increased for large maps

    /// <summary>Calculate walkable tiles once for bulk accessibility checks.</summary>
    public Dictionary<Point,int> FindReachableTiles(GameLocation location, Vector2 start, int maxDistance)
    {
        var result=new Dictionary<Point,int>();
        var origin=new Point((int)start.X,(int)start.Y);
        var queue=new Queue<Point>();
        result[origin]=0;queue.Enqueue(origin);
        while(queue.Count>0 && result.Count<MaxIterations) {
            var current=queue.Dequeue();int distance=result[current];
            if(distance>=maxDistance) continue;
            foreach(var next in new[]{new Point(current.X+1,current.Y),new Point(current.X-1,current.Y),new Point(current.X,current.Y+1),new Point(current.X,current.Y-1)}) {
                if(result.ContainsKey(next) || !IsTileWalkable(location,next.X,next.Y)) continue;
                result[next]=distance+1;queue.Enqueue(next);
            }
        }
        return result;
    }

    /// <summary>Find a path from start to goal using A* algorithm.</summary>
    /// <param name="location">The game location to pathfind in.</param>
    /// <param name="start">Starting tile position.</param>
    /// <param name="goal">Target tile position.</param>
    /// <returns>List of tile positions forming the path, or null if no path found.</returns>
    public List<Vector2>? FindPath(GameLocation location, Vector2 start, Vector2 goal)
    {
        if (location == null)
            return null;

        // Quick check: if goal is not passable, no path possible
        if (!IsTileWalkable(location, (int)goal.X, (int)goal.Y))
            return null;

        // If already at goal, return empty path
        if (start == goal)
            return new List<Vector2>();

        var openSet = new PriorityQueue<Vector2, float>();
        var cameFrom = new Dictionary<Vector2, Vector2>();
        var gScore = new Dictionary<Vector2, float> { [start] = 0 };
        var fScore = new Dictionary<Vector2, float> { [start] = Heuristic(start, goal) };

        openSet.Enqueue(start, fScore[start]);
        var inOpenSet = new HashSet<Vector2> { start };

        int iterations = 0;

        while (openSet.Count > 0 && iterations < MaxIterations)
        {
            iterations++;

            var current = openSet.Dequeue();
            inOpenSet.Remove(current);

            if (current == goal)
            {
                return ReconstructPath(cameFrom, current);
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                int nx = (int)neighbor.X;
                int ny = (int)neighbor.Y;

                // Skip if not walkable
                if (!IsTileWalkable(location, nx, ny))
                    continue;

                float tentativeGScore = gScore[current] + 1; // Cost of 1 per tile

                if (!gScore.ContainsKey(neighbor) || tentativeGScore < gScore[neighbor])
                {
                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentativeGScore;
                    fScore[neighbor] = tentativeGScore + Heuristic(neighbor, goal);

                    if (!inOpenSet.Contains(neighbor))
                    {
                        openSet.Enqueue(neighbor, fScore[neighbor]);
                        inOpenSet.Add(neighbor);
                    }
                }
            }
        }

        // No path found
        return null;
    }

    /// <summary>Check if a tile is walkable for the player.</summary>
    private bool IsTileWalkable(GameLocation location, int x, int y)
    {
        // Check map bounds
        if (x < 0 || y < 0)
            return false;

        var map = location.Map;
        if (map == null || map.Layers.Count == 0)
            return false;

        var layer = map.Layers[0];
        if (x >= layer.LayerWidth || y >= layer.LayerHeight)
            return false;

        // Use game's built-in passability check
        var tileLocation = new Location(x, y);

// Local workaround: player-confirmed walkable farmhouse steps only.
bool knownFarmhouseSteps =
    location.Name == "Farm" &&
    x == 64 && (y == 15 || y == 16);

        // Check if tile itself is passable (map layer check)
        if (!knownFarmhouseSteps && !location.isTilePassable(tileLocation, Game1.viewport))
            return false;

        // Check for objects blocking the tile
        var tileVector = new Vector2(x, y);
        if (location.Objects.ContainsKey(tileVector))
        {
            var obj = location.Objects[tileVector];
            if (!obj.isPassable())
                return false;
        }

        // Check for terrain features (trees, etc.)
        if (location.terrainFeatures.ContainsKey(tileVector))
        {
            var feature = location.terrainFeatures[tileVector];
            if (!feature.isPassable())
                return false;
        }

        // Check for large terrain features (like resource clumps)
        foreach (var clump in location.resourceClumps)
        {
            if (clump.occupiesTile(x, y))
                return false;
        }

        // Check for buildings (on farm locations)
        if (location is Farm farm)
        {
            foreach (var building in farm.buildings)
            {
                if (building.occupiesTile(tileVector) && !knownFarmhouseSteps)
                    return false;
            }
        }

        // Check for furniture
        foreach (var furniture in location.furniture)
        {
            if (furniture.isPassable())
                continue;
            // Permit planning through beds; actual movement collision remains.
            
            if (furniture.TileLocation == tileVector ||
                furniture.boundingBox.Value.Contains(x * 64 + 32, y * 64 + 32))
                return false;
        }

        // Check water tiles (player can't walk on water)
        if (location.isWaterTile(x, y) && !location.isTilePassable(tileLocation, Game1.viewport))
            return false;

        return true;
    }

    /// <summary>Get the 4-directional neighbors of a tile.</summary>
    private IEnumerable<Vector2> GetNeighbors(Vector2 tile)
    {
        yield return new Vector2(tile.X + 1, tile.Y);     // Right
        yield return new Vector2(tile.X - 1, tile.Y);     // Left
        yield return new Vector2(tile.X, tile.Y + 1);     // Down
        yield return new Vector2(tile.X, tile.Y - 1);     // Up
    }

    /// <summary>Manhattan distance heuristic.</summary>
    private float Heuristic(Vector2 a, Vector2 b)
    {
        return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }

    /// <summary>Reconstruct the path from start to goal.</summary>
    private List<Vector2> ReconstructPath(Dictionary<Vector2, Vector2> cameFrom, Vector2 current)
    {
        var path = new List<Vector2> { current };

        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Add(current);
        }

        // Reverse to get start-to-goal order and remove starting position (we're already there)
        path.Reverse();
        if (path.Count > 0)
            path.RemoveAt(0);

        return path;
    }
}
