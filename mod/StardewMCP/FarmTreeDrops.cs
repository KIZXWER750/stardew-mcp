using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private static Dictionary<string,int> SnapshotFarmInventory()
    {
        return Game1.player.Items.Where(item=>item!=null)
            .GroupBy(item=>item!.QualifiedItemId)
            .ToDictionary(group=>group.Key,group=>group.Sum(item=>item!.Stack));
    }

    private static int FarmInventoryCount(string qualifiedItemId)
    {
        return Game1.player.Items.Where(item=>item!=null && (item.QualifiedItemId==qualifiedItemId || item.ItemId==qualifiedItemId))
            .Sum(item=>item!.Stack);
    }

    private static bool IsCollectibleFarmDebris(Debris debris)
    {
        return debris.Chunks!=null && debris.Chunks.Count>0 &&
            debris.debrisType.Value is Debris.DebrisType.OBJECT
                or Debris.DebrisType.RESOURCE or Debris.DebrisType.ARCHAEOLOGY;
    }

    private static Item? FarmDebrisItem(Debris debris)
    {
        if(debris.item!=null) return debris.item;
        string id=debris.itemId.Value ?? "";
        if(id=="") return null;
        try { return ItemRegistry.Create(id,Math.Max(1,debris.Chunks.Count),debris.itemQuality); }
        catch { return null; }
    }

    private static Point? FarmDebrisTile(Debris debris)
    {
        var chunk=debris.Chunks?.FirstOrDefault();
        if(chunk==null) return null;
        Vector2 position=chunk.position.Value;
        return new Point((int)Math.Floor(position.X/Game1.tileSize),(int)Math.Floor(position.Y/Game1.tileSize));
    }

    private static bool FarmDropMatchesFilter(FarmJob j,Item? item)
    {
        if(j.DropItemFilter=="") return true;
        return item!=null && (item.QualifiedItemId==j.DropItemFilter || item.ItemId==j.DropItemFilter);
    }

    private List<Debris> TreeDropCandidates(FarmJob j)
    {
        return Game1.currentLocation.debris.Where(IsCollectibleFarmDebris).Where(debris=> {
            var tile=FarmDebrisTile(debris);
            var item=FarmDebrisItem(debris);
            return tile.HasValue && j.TreeOrigins.Any(origin=>
                Math.Abs(origin.X-tile.Value.X)<=j.DropSearchRadius && Math.Abs(origin.Y-tile.Value.Y)<=j.DropSearchRadius)
                && FarmDropMatchesFilter(j,item);
        }).ToList();
    }

    private CommandResponse FarmCollectStart(GameCommand c,FarmJob j,string requestId,string fingerprint)
    {
        if(j.Location!="Farm" || Game1.currentLocation.Name!="Farm") throw new InvalidOperationException("collect_loose_items currently requires Farm");
        int radius=c.Params.TryGetValue("search_radius",out var radiusValue)?GetIntParam(radiusValue):20;
        if(radius<1 || radius>30) throw new InvalidOperationException("search_radius must be 1..30");
        int maxObstacles=c.Params.TryGetValue("max_obstacles",out var maximum)?GetIntParam(maximum):8;
        if(maxObstacles<0 || maxObstacles>16) throw new InvalidOperationException("max_obstacles must be 0..16");
        string filter=c.Params.TryGetValue("item_id",out var itemValue)?GetStringParam(itemValue):"";
        int desired=c.Params.TryGetValue("desired_inventory_quantity",out var desiredValue)?GetIntParam(desiredValue):0;
        if(desired<0 || desired>9999) throw new InvalidOperationException("desired_inventory_quantity must be 0..9999");
        if(filter.StartsWith("(O)")) filter=filter.Substring(3);
        j.DropItemFilter=filter;j.DropSearchRadius=radius;j.DesiredInventoryQuantity=desired;j.MaxDropAccessObstacles=maxObstacles;
        j.TreeOrigins=new List<Point>{new((int)Game1.player.Tile.X,(int)Game1.player.Tile.Y)};j.TreeInventoryBefore=SnapshotFarmInventory();
        j.InventoryQuantityBeforeCollection=filter==""?0:FarmInventoryCount(filter);j.InventoryQuantityAfterCollection=j.InventoryQuantityBeforeCollection;
        j.TotalTargets=0;j.CompletedTargets=0;j.AlreadySatisfiedTargets=0;j.Deadline=DateTime.UtcNow.AddMinutes(5);j.Lease=DateTime.UtcNow.AddSeconds(20);j.StartDate=FarmDate();
        j.RequestId=requestId;j.CollectingDrops=true;j.Phase="TREE_DROP_SETTLE";j.NextActionAfter=DateTime.UtcNow.AddMilliseconds(500);j.DropQuietSince=default;
        j.DropEvidence.Add($"loose-item scan radius={radius}, item_filter={(filter==""?"ANY":filter)}, desired_inventory={desired}");
        _farmJob=j;_farmHistory[j.TaskId]=j;if(requestId!="") _farmRequests[requestId]=(fingerprint,j);SaveFarm(j);return FarmReply(c,j);
    }

    private void RecordTreeInventoryDelta(FarmJob j)
    {
        var after=SnapshotFarmInventory();
        foreach(var pair in after.OrderBy(pair=>pair.Key)) {
            int before=j.TreeInventoryBefore.TryGetValue(pair.Key,out int value)?value:0;
            int gained=pair.Value-before;
            if(gained>0) {
                string evidence=$"inventory gained {pair.Key} x{gained}";
                if(!j.DropEvidence.Contains(evidence)) j.DropEvidence.Add(evidence);
            }
        }
    }

    private void BeginTreeDropCollection(FarmJob j,DateTime now)
    {
        j.CollectingDrops=true;
        j.Phase="TREE_DROP_SETTLE";
        j.NextActionAfter=now.AddMilliseconds(2600);
        j.DropQuietSince=default;
        j.TargetDebris=null;
        j.DropEvidence.Add("tree and stump removed; scanning loose location.debris drops before selecting another tree");
        SaveFarm(j);
        _monitor.Log($"[TREE DROPS] {j.TaskId} waiting for falling-tree drops to settle",LogLevel.Info);
    }

    private IEnumerable<Point> PickupPositions(Point drop)
    {
        var layer=Game1.currentLocation.Map.Layers[0];
        // Contact or cardinal/diagonal adjacency is reliable even with no
        // magnetism bonus. Don't mistake a merely nearby tile for pickup range.
        for(int radius=0;radius<=1;radius++)
            for(int y=drop.Y-radius;y<=drop.Y+radius;y++)
                for(int x=drop.X-radius;x<=drop.X+radius;x++) {
                    if(Math.Max(Math.Abs(x-drop.X),Math.Abs(y-drop.Y))!=radius) continue;
                    if(x<0 || y<0 || x>=layer.LayerWidth || y>=layer.LayerHeight) continue;
                    yield return new Point(x,y);
                }
    }

    private bool QueueTreeDropPickupMove(FarmJob j,Debris debris,DateTime now)
    {
        var drop=FarmDebrisTile(debris);
        if(!drop.HasValue) return false;
        var player=Game1.player;
        var debrisItem=FarmDebrisItem(debris);
        j.TargetDropItemId=debrisItem?.QualifiedItemId ?? "";
        j.TargetDropInventoryBefore=j.TargetDropItemId==""?0:FarmInventoryCount(j.TargetDropItemId);
        var playerTile=new Point((int)player.Tile.X,(int)player.Tile.Y);
        if(Math.Max(Math.Abs(playerTile.X-drop.Value.X),Math.Abs(playerTile.Y-drop.Value.Y))<=1) {
            j.TargetDebris=debris;j.DropMoveAttempts++;
            j.NextActionAfter=now.AddMilliseconds(1400);j.Phase="TREE_DROP_WAIT";
            _monitor.Log($"[TREE DROP WAIT] already within pickup range of ({drop.Value.X},{drop.Value.Y})",LogLevel.Info);
            return true;
        }
        var positions=PickupPositions(drop.Value)
            .Select(point=>(Point:point,Path:_pathfinder.FindPathWithClearingCosts(Game1.currentLocation,player.Tile,new Vector2(point.X,point.Y),DropClearingCost,shortestDistance:true)))
            .Where(item=>item.Path!=null)
            .OrderBy(item=>item.Path!.Count).ToList();
        if(positions.Count==0) return false;
        var route=positions[0].Path!;
        var obstacles=route.Where(step=>!_pathfinder.IsWalkable(Game1.currentLocation,(int)step.X,(int)step.Y)).ToList();
        if(obstacles.Count>j.MaxDropAccessObstacles-j.ObstaclesClearedForDrops) {
            FinishFarm(j,"BLOCKED","DROP_CLEARING_LIMIT: shortest route exceeds remaining obstacle budget");return true;
        }
        if(obstacles.Count>0) {
            var first=obstacles[0];
            return QueueTreeDropObstacle(j,new Point((int)first.X,(int)first.Y),now);
        }
        var destination=positions[0].Point;
        j.TargetDebris=debris;j.DropMoveAttempts++;j.Phase="TREE_DROP_MOVING";j.PhaseStarted=now;
        ExecuteMoveTo(new GameCommand { Id=j.TaskId,Action="move_to",Params=new(){{"x",destination.X},{"y",destination.Y}},OnComplete=response=> {
            if(j.Status!="RUNNING") return;
            if(response.Success) {
                j.NextActionAfter=DateTime.UtcNow.AddMilliseconds(1400);
                j.Phase="TREE_DROP_WAIT";
            } else {
                j.Phase="TREE_DROP_SELECT";
            }
        }});
        _monitor.Log($"[TREE DROP MOVE] drop=({drop.Value.X},{drop.Value.Y}) destination=({destination.X},{destination.Y})",LogLevel.Info);
        return true;
    }

    private int? DropClearingCost(int x,int y)
    {
        var tile=ReadFarmTile(x,y);
        var location=Game1.currentLocation;
        // An obstacle must not conceal a wall, crop, placed object, or large feature.
        if(tile.HasCrop || tile.Hoed || !location.isTilePassable(new xTile.Dimensions.Location(x,y),Game1.viewport)
            || location.largeTerrainFeatures.Any(feature=>feature.getBoundingBox().Intersects(new Rectangle(x*64,y*64,64,64)))) return null;
        bool tree=tile.IsWildTree && tile.Obstacle.StartsWith("protected wild tree");
        bool removable=tree || TravelCanClear(tile);
        if(!removable) return null;
        string tool=tree?"Axe":tile.ClearTool;
        if(tool=="Scythe" && tile.Obstacle=="weed") tool="Pickaxe";
        return FarmToolSlot(tool)>=0?1:null;
    }

    private bool QueueTreeDropObstacle(FarmJob j,Point obstacle,DateTime now)
    {
        if(j.ObstaclesClearedForDrops>=j.MaxDropAccessObstacles) return false;
        if(!DropClearingCost(obstacle.X,obstacle.Y).HasValue) return false;
        var tile=ReadFarmTile(obstacle.X,obstacle.Y);
        var approaches=ReadFarmApproaches(obstacle.X,obstacle.Y).Where(a=>a.Reachable).OrderBy(a=>a.PathLength).ToList();
        if(approaches.Count==0) return false;
        j.Target=obstacle;
        if(tile.IsWildTree && !j.TreeOrigins.Contains(j.Target)) j.TreeOrigins.Add(j.Target);
        j.Action=tile.IsWildTree?"Axe":tile.ClearTool;
        j.ActionWasTree=tile.IsWildTree;
        j.Approaches=approaches.Select(a=>new Point(a.X,a.Y)).ToList();
        j.ReturnPhase="TREE_DROP_SELECT";j.Phase="APPROACH";j.PhaseStarted=now;j.Attempts=0;
        SaveFarm(j);
        _monitor.Log($"[TREE DROP CLEAR] shortest-route obstacle=({obstacle.X},{obstacle.Y}) tool={j.Action}",LogLevel.Info);
        return true;
    }

    private void UpdateTreeDropCollection(FarmJob j,DateTime now)
    {
        if(now<j.NextActionAfter) return;
        if(Game1.activeClickableMenu!=null || Game1.player.UsingTool || !Game1.player.CanMove) return;
        if(Game1.player.Stamina<j.MinimumEnergy+4) {FinishFarm(j,"PAUSED","LOW_ENERGY");return;}
        if(j.Phase=="TREE_DROP_SETTLE") {j.Phase="TREE_DROP_SELECT";return;}
        if(j.Phase=="TREE_DROP_MOVING") {
            if((now-j.PhaseStarted).TotalSeconds>15) j.Phase="TREE_DROP_SELECT";
            return;
        }
        if(j.Phase=="TREE_DROP_WAIT") {
            int after=j.TargetDropItemId==""?j.TargetDropInventoryBefore:FarmInventoryCount(j.TargetDropItemId);
            if(j.TargetDebris==null || !Game1.currentLocation.debris.Contains(j.TargetDebris)) {
                if(j.TargetDropItemId=="" || after<=j.TargetDropInventoryBefore) {
                    FinishFarm(j,"BLOCKED","DROP_DISAPPEARED_WITHOUT_INVENTORY_GAIN");return;
                }
                int gained=after-j.TargetDropInventoryBefore;
                j.DropItemsCollected+=gained;
                j.DropsCollected++;j.DropMoveAttempts=0;
                j.DropEvidence.Add($"collected {j.TargetDropItemId} x{gained}; inventory {j.TargetDropInventoryBefore}->{after}");
                j.TargetDebris=null;j.Phase="TREE_DROP_SELECT";SaveFarm(j);return;
            }
            // A Debris entity may contain several independently moving chunks.
            // Keep following it when at least one chunk was collected instead
            // of treating the still-present entity as a failed pickup.
            if(j.TargetDropItemId!="" && after>j.TargetDropInventoryBefore) {
                int gained=after-j.TargetDropInventoryBefore;
                j.DropItemsCollected+=gained;
                j.DropEvidence.Add($"partial pickup {j.TargetDropItemId} x{gained}; debris still moving");
                j.TargetDropInventoryBefore=after;j.DropMoveAttempts=0;
                j.Phase="TREE_DROP_SELECT";SaveFarm(j);return;
            }
            if(j.DropMoveAttempts<12) {j.Phase="TREE_DROP_SELECT";return;}
            var stuckTile=FarmDebrisTile(j.TargetDebris);
            FinishFarm(j,"BLOCKED",stuckTile.HasValue
                ? $"DROP_NOT_COLLECTED at ({stuckTile.Value.X},{stuckTile.Value.Y}) after verified approach"
                : "DROP_POSITION_UNAVAILABLE");
            return;
        }
        if(j.Phase!="TREE_DROP_SELECT") return;
        if(j.DropItemFilter!="") {
            j.InventoryQuantityAfterCollection=FarmInventoryCount(j.DropItemFilter);
            if(j.DesiredInventoryQuantity>0 && j.InventoryQuantityAfterCollection>=j.DesiredInventoryQuantity) {RecordTreeInventoryDelta(j);FinishFarm(j,"COMPLETED",$"Desired inventory reached from loose items: {j.InventoryQuantityAfterCollection}/{j.DesiredInventoryQuantity}");return;}
        }
        var drops=TreeDropCandidates(j);
        j.DropsDetected=Math.Max(j.DropsDetected,j.DropsCollected+drops.Count);
        if(drops.Count==0) {
            if(j.DropQuietSince==default) {j.DropQuietSince=now;j.NextActionAfter=now.AddMilliseconds(1200);return;}
            if((now-j.DropQuietSince).TotalMilliseconds<1200) return;
            RecordTreeInventoryDelta(j);
            j.InventoryQuantityAfterCollection=j.DropItemFilter==""?0:FarmInventoryCount(j.DropItemFilter);
            string reason=j.Operation=="collect"?$"All matching loose items exhausted; inventory={j.InventoryQuantityAfterCollection}, desired={j.DesiredInventoryQuantity}, debris={j.DropsCollected}, item_units={j.DropItemsCollected}":$"All trees/stumps removed and nearby loose drops collected; debris={j.DropsCollected}, item_units={j.DropItemsCollected}, access_obstacles={j.ObstaclesClearedForDrops}";
            if(j.Operation=="trees" && j.CollectionOrigin.HasValue) {
                j.CollectedTreeTargets.Add(j.CollectionOrigin.Value);
                j.CollectionOrigin=null;j.CollectingDrops=false;j.TargetDebris=null;
                j.TreeOrigins=j.Targets.ToList();j.Phase="SELECT";
                SaveFarm(j);
                if(j.CompletedTargets<j.TotalTargets) return;
            }
            FinishFarm(j,"COMPLETED",reason);
            return;
        }
        j.DropQuietSince=default;
        var ordered=drops.Select(drop=>(Drop:drop,Tile:FarmDebrisTile(drop)))
            .Where(item=>item.Tile.HasValue)
            .OrderByDescending(item=>Math.Abs(item.Tile!.Value.X-Game1.player.Tile.X)+Math.Abs(item.Tile.Value.Y-Game1.player.Tile.Y)).ToList();
        if(ordered.Count==0) {FinishFarm(j,"BLOCKED","DROP_POSITION_UNAVAILABLE");return;}
        var chosen=ordered[0];
        var item=FarmDebrisItem(chosen.Drop);
        if(item==null) {FinishFarm(j,"BLOCKED","DROP_ITEM_UNAVAILABLE");return;}
        if(!Game1.player.couldInventoryAcceptThisItem(item)) {FinishFarm(j,"PAUSED","INVENTORY_FULL_DURING_DROP_COLLECTION");return;}
        if(QueueTreeDropPickupMove(j,chosen.Drop,now)) return;

        FinishFarm(j,"BLOCKED",$"DROP_UNREACHABLE_PROTECTED_PATH at ({chosen.Tile.Value.X},{chosen.Tile.Value.Y})");
    }
}
