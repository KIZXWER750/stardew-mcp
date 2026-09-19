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
        return Game1.player.Items.Where(item=>item!=null && item.QualifiedItemId==qualifiedItemId)
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

    private List<Debris> TreeDropCandidates(FarmJob j)
    {
        return Game1.currentLocation.debris.Where(IsCollectibleFarmDebris).Where(debris=> {
            var tile=FarmDebrisTile(debris);
            return tile.HasValue && j.TreeOrigins.Any(origin=>
                Math.Abs(origin.X-tile.Value.X)<=8 && Math.Abs(origin.Y-tile.Value.Y)<=8);
        }).ToList();
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
        j.DropEvidence.Add("all selected ordinary trees are gone; scanning loose location.debris drops");
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
            .Select(point=>(Point:point,Path:_pathfinder.FindPath(Game1.currentLocation,player.Tile,new Vector2(point.X,point.Y))))
            .Where(item=>item.Path!=null)
            .OrderBy(item=>item.Path!.Count).ToList();
        if(positions.Count==0) return false;
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

    private bool QueueTreeDropObstacle(FarmJob j,Point drop,DateTime now)
    {
        if(j.ObstaclesClearedForDrops>=24) return false;
        var layer=Game1.currentLocation.Map.Layers[0];
        int px=(int)Game1.player.Tile.X,py=(int)Game1.player.Tile.Y;
        int minX=Math.Max(0,Math.Min(px,drop.X)-2),maxX=Math.Min(layer.LayerWidth-1,Math.Max(px,drop.X)+2);
        int minY=Math.Max(0,Math.Min(py,drop.Y)-2),maxY=Math.Min(layer.LayerHeight-1,Math.Max(py,drop.Y)+2);
        var candidates=new List<(FarmTile Tile,List<FarmApproach> Approaches,int Score)>();
        for(int y=minY;y<=maxY;y++) for(int x=minX;x<=maxX;x++) {
            var tile=ReadFarmTile(x,y);
            // FruitTree, crops, HoeDirt, buildings, machines, furniture and
            // resource clumps never expose a clearing tool here.
            bool removable=tile.IsWildTree || (tile.ClearTool!="" && !tile.HasCrop && !tile.Hoed);
            if(!removable) continue;
            var approaches=ReadFarmApproaches(x,y).Where(a=>a.Reachable).ToList();
            if(approaches.Count==0) continue;
            int score=Math.Abs(x-drop.X)+Math.Abs(y-drop.Y)+Math.Abs(x-px)+Math.Abs(y-py);
            candidates.Add((tile,approaches,score));
        }
        var selected=candidates.OrderBy(item=>item.Score).FirstOrDefault();
        if(selected.Tile==null) return false;
        j.Target=new Point(selected.Tile.X,selected.Tile.Y);
        if(selected.Tile.IsWildTree && !j.TreeOrigins.Contains(j.Target)) j.TreeOrigins.Add(j.Target);
        j.Action=selected.Tile.IsWildTree?"Axe":selected.Tile.ClearTool;
        j.ActionWasTree=selected.Tile.IsWildTree;
        j.Approaches=selected.Approaches.OrderBy(a=>a.PathLength).Select(a=>new Point(a.X,a.Y)).ToList();
        j.ReturnPhase="TREE_DROP_SELECT";j.Phase="APPROACH";j.PhaseStarted=now;j.Attempts=0;
        _monitor.Log($"[TREE DROP CLEAR] drop=({drop.X},{drop.Y}) obstacle=({j.Target.X},{j.Target.Y}) type={selected.Tile.Obstacle} tool={j.Action}",LogLevel.Info);
        return true;
    }

    private void UpdateTreeDropCollection(FarmJob j,DateTime now)
    {
        if(now<j.NextActionAfter) return;
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
        var drops=TreeDropCandidates(j);
        j.DropsDetected=Math.Max(j.DropsDetected,j.DropsCollected+drops.Count);
        if(drops.Count==0) {
            if(j.DropQuietSince==default) {j.DropQuietSince=now;j.NextActionAfter=now.AddMilliseconds(1200);return;}
            if((now-j.DropQuietSince).TotalMilliseconds<1200) return;
            RecordTreeInventoryDelta(j);
            FinishFarm(j,"COMPLETED",$"All trees/stumps removed and nearby loose drops collected; debris={j.DropsCollected}, item_units={j.DropItemsCollected}, access_obstacles={j.ObstaclesClearedForDrops}");
            return;
        }
        j.DropQuietSince=default;
        var ordered=drops.Select(drop=>(Drop:drop,Tile:FarmDebrisTile(drop)))
            .Where(item=>item.Tile.HasValue)
            .OrderBy(item=>Math.Abs(item.Tile!.Value.X-Game1.player.Tile.X)+Math.Abs(item.Tile.Value.Y-Game1.player.Tile.Y)).ToList();
        if(ordered.Count==0) {FinishFarm(j,"BLOCKED","DROP_POSITION_UNAVAILABLE");return;}
        var chosen=ordered[0];
        var item=FarmDebrisItem(chosen.Drop);
        if(item==null) {FinishFarm(j,"BLOCKED","DROP_ITEM_UNAVAILABLE");return;}
        if(!Game1.player.couldInventoryAcceptThisItem(item)) {FinishFarm(j,"PAUSED","INVENTORY_FULL_DURING_DROP_COLLECTION");return;}
        if(QueueTreeDropPickupMove(j,chosen.Drop,now)) return;
        if(QueueTreeDropObstacle(j,chosen.Tile!.Value,now)) return;
        FinishFarm(j,"BLOCKED",$"DROP_UNREACHABLE_PROTECTED_PATH at ({chosen.Tile.Value.X},{chosen.Tile.Value.Y})");
    }
}
