using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private bool TravelCanClear(FarmTile tile)
    {
        if(tile.HasCrop || tile.Hoed) return false;
        if(tile.IsWildTree)
            return tile.IsTreeStump || tile.GrowthStage<5;
        return (tile.Obstacle is "grass" or "weed" or "twig" or "small stone") && tile.ClearTool!="";
    }

    private bool FarmActionCanClearTree(FarmJob job,FarmTile tile)
        => tile.IsWildTree && (job.Operation=="trees" || job.Operation=="travel" && TravelCanClear(tile));

    private int? TravelClearingCost(int x,int y)
    {
        var tile=ReadFarmTile(x,y);
        if(!TravelCanClear(tile)) return null;
        if(tile.IsTreeStump) return 8;
        if(tile.IsWildTree) return 5;
        return tile.Obstacle=="grass" || tile.Obstacle=="weed" ? 2 : 3;
    }

    private CommandResponse FarmTravelStart(GameCommand c,FarmJob j,string requestId,string fingerprint)
    {
        if(j.Location!="Farm" || Game1.currentLocation.Name!="Farm")
            throw new InvalidOperationException("move_with_clearing currently requires Farm");
        if(j.Width!=1 || j.Height!=1)
            throw new InvalidOperationException("move_with_clearing destination must be one tile");
        int maxObstacles=c.Params.TryGetValue("max_obstacles",out var maximum)?GetIntParam(maximum):8;
        if(maxObstacles<1 || maxObstacles>16)
            throw new InvalidOperationException("max_obstacles must be 1..16");

        var start=Game1.player.Tile;
        var destination=new Vector2(j.X,j.Y);
        var path=_pathfinder.FindPathWithClearingCosts(Game1.currentLocation,start,destination,TravelClearingCost);
        if(path==null)
            throw new InvalidOperationException($"NO_SAFE_CLEARING_ROUTE to ({j.X},{j.Y}); protected obstacle or map collision remains");

        var obstacles=new List<Point>();
        foreach(var step in path) {
            int x=(int)step.X,y=(int)step.Y;
            if(_pathfinder.IsWalkable(Game1.currentLocation,x,y)) continue;
            var point=new Point(x,y);
            if(!obstacles.Contains(point)) obstacles.Add(point);
        }
        if(obstacles.Count>maxObstacles)
            throw new InvalidOperationException($"CLEARING_LIMIT: route requires {obstacles.Count} obstacles, maximum {maxObstacles}");

        j.DestinationX=j.X;j.DestinationY=j.Y;
        j.PlannedPathLength=path.Count;j.PlannedObstacles=obstacles.Count;
        j.Targets=obstacles;j.TotalTargets=obstacles.Count;
        j.TravelEvidence.Add($"planned weighted route length={path.Count}, removable_obstacles={obstacles.Count}");
        RefreshFarm(j);j.AlreadySatisfiedTargets=j.CompletedTargets;
        j.Deadline=DateTime.UtcNow.AddMinutes(5);j.Lease=DateTime.UtcNow.AddSeconds(20);j.StartDate=FarmDate();
        j.RequestId=requestId;
        _farmJob=j;_farmHistory[j.TaskId]=j;
        if(requestId!="") _farmRequests[requestId]=(fingerprint,j);
        SaveFarm(j);
        return FarmReply(c,j);
    }

    private void BeginFarmTravelFinal(FarmJob j,DateTime now)
    {
        if(j.TravelFinalStarted) return;
        j.TravelFinalStarted=true;j.Phase="TRAVEL_MOVING";j.PhaseStarted=now;
        j.TravelEvidence.Add($"all planned obstacles cleared; moving to ({j.DestinationX},{j.DestinationY})");
        ExecuteMoveTo(new GameCommand {Id=j.TaskId,Action="move_to",Params=new() {
            {"x",j.DestinationX},{"y",j.DestinationY}
        },OnComplete=response=> {
            if(j.Status!="RUNNING") return;
            int x=(int)Game1.player.Tile.X,y=(int)Game1.player.Tile.Y;
            if(response.Success && x==j.DestinationX && y==j.DestinationY) {
                j.TravelEvidence.Add($"arrival verified at ({x},{y})");
                FinishFarm(j,"COMPLETED","Destination reached after verified safe clearing");
            } else {
                FinishFarm(j,"BLOCKED",$"FINAL_MOVE_FAILED: {response.Message}; actual=({x},{y})");
            }
        }});
    }
}
