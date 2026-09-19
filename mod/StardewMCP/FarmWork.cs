using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewMCP;

public partial class CommandExecutor
{
    private FarmJob? _farmJob;
    private readonly Dictionary<string, FarmJob> _farmHistory = new();
    private readonly Dictionary<string, (string Fingerprint, FarmJob Job)> _farmRequests = new();

    public class FarmTile
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool Diggable { get; set; }
        public bool Hoed { get; set; }
        public bool Watered { get; set; }
        public bool HasCrop { get; set; }
        public string CropSeedId { get; set; } = "";
        public bool ReadyForHarvest { get; set; }
        public bool Dead { get; set; }
        public bool IsWildTree { get; set; }
        public bool IsTreeStump { get; set; }
        public int GrowthStage { get; set; }
        public string TreeState { get; set; } = "";
        public string RequiredTool { get; set; } = "";
        public string RemovalSequence { get; set; } = "";
        public string HarvestMethod { get; set; } = "";
        public string Terrain { get; set; } = "none";
        public string Obstacle { get; set; } = "";
        public string ClearTool { get; set; } = "";
        public string Progress { get; set; } = "";
        public int ReachableApproachCount { get; set; }
        public List<FarmApproach> Approaches { get; set; } = new();
    }

    public class FarmApproach
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string Face { get; set; } = "";
        public bool InBounds { get; set; }
        public bool Reachable { get; set; }
        public int PathLength { get; set; } = -1;
    }

    public class PlotCandidate
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int TotalTiles { get; set; }
        public int AccessibleTargets { get; set; }
        public int RemovableObstacles { get; set; }
        public int ExistingHoeDirt { get; set; }
        public int DistanceFromAnchor { get; set; }
        public string AccessStatus { get; set; } = "";
        public List<string> Conflicts { get; set; } = new();
    }

    public class FarmJob
    {
        public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
        public string RequestId { get; set; } = "";
        public string Operation { get; set; } = "";
        public string Location { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int MinimumEnergy { get; set; }
        public int StopTime { get; set; }
        public string Status { get; set; } = "RUNNING";
        public string Reason { get; set; } = "";
        public string Phase { get; set; } = "SELECT";
        public int TotalTargets { get; set; }
        public int CompletedTargets { get; set; }
        public int AlreadySatisfiedTargets { get; set; }
        public int RemainingTargets => TotalTargets - CompletedTargets;
        public int ToolUses { get; set; }
        public int PlacementUses { get; set; }
        public int InteractionUses { get; set; }
        public int SeedsConsumed { get; set; }
        public int WaterBefore { get; set; }
        public int WaterAfter { get; set; }
        internal bool Refilled;
        internal int WorkRevision => ToolUses + PlacementUses + InteractionUses;
        public string SeedItemId { get; set; } = "";
        public string ExistingCropPolicy { get; set; } = "PRESERVE_AND_REPORT";
        public int MaxTrees { get; set; } = 3;
        public bool PreserveYoungTrees { get; set; }
        public int DropsDetected { get; set; }
        public int DropsCollected { get; set; }
        public int DropItemsCollected { get; set; }
        public int ObstaclesClearedForDrops { get; set; }
        public List<string> DropEvidence { get; set; } = new();
        public string DropItemFilter { get; set; } = "";
        public int DropSearchRadius { get; set; } = 8;
        public int DesiredInventoryQuantity { get; set; }
        public int InventoryQuantityBeforeCollection { get; set; }
        public int InventoryQuantityAfterCollection { get; set; }
        public int DestinationX { get; set; }
        public int DestinationY { get; set; }
        public int PlannedPathLength { get; set; }
        public int PlannedObstacles { get; set; }
        public int ClearedTravelObstacles { get; set; }
        public List<string> TravelEvidence { get; set; } = new();
        public List<string> ExcludedTiles { get; set; } = new();
        public List<string> HarvestEvidence { get; set; } = new();
        internal HashSet<Point> Harvested = new();
        internal bool ClearingPhase;
        public string CheckpointError { get; set; } = "";
        public List<string> Issues { get; set; } = new();
        internal Dictionary<Point,int> Deferred = new();
        public List<FarmTile> Tiles { get; set; } = new();
        // Internal execution state is intentionally excluded from JSON.
        internal List<Point> Targets = new();
        internal Point Target, Stand;
        internal string Action = "", Before = "";
        internal int Attempts;
        internal float EnergyBeforeAction;
        internal int TreeHitLimit;
        internal bool ActionWasTree;
        internal bool CollectingDrops;
        internal Point? CollectionOrigin;
        internal HashSet<Point> CollectedTreeTargets = new();
        internal string ReturnPhase = "";
        internal List<Point> TreeOrigins = new();
        internal Debris? TargetDebris;
        internal string TargetDropItemId = "";
        internal int TargetDropInventoryBefore;
        internal int DropMoveAttempts;
        internal int MaxDropAccessObstacles = 24;
        internal DateTime DropQuietSince;
        internal Dictionary<string,int> TreeInventoryBefore = new();
        internal bool TravelFinalStarted;
        internal string TravelObstacleBefore = "";
        internal bool SawBusy;
        internal DateTime Deadline, PhaseStarted, Lease, IdleSince, NextActionAfter;
        internal string StartDate = "";
        internal List<Point> Approaches = new();
    }

    private static string FarmDate() => $"{Game1.year}:{Game1.currentSeason}:{Game1.dayOfMonth}";
    private bool FarmActive => _farmJob?.Status == "RUNNING";
    private static bool Satisfied(FarmJob j, FarmTile t) => j.Operation switch {
        "refill" => j.Refilled,
        "clear" => t.Obstacle=="" || t.Hoed || t.HasCrop,
        "prepare" or "till" => t.Hoed,
        "plant" => t.HasCrop && t.CropSeedId==j.SeedItemId,
        "harvest" => j.Harvested.Contains(new Point(t.X,t.Y)),
        "trees" => !t.IsWildTree,
        "travel" => t.Obstacle=="" && !t.IsWildTree,
        _ => t.Hoed && t.Watered
    };
    private bool InFarmArea(FarmJob j, int x, int y) => x >= j.X && x < j.X+j.Width && y >= j.Y && y < j.Y+j.Height;

    private FarmJob ParseFarmArea(GameCommand c)
    {
        int N(string key, int fallback = 0) => c.Params.TryGetValue(key, out var value) ? GetIntParam(value) : fallback;
        var j = new FarmJob {
            Location = c.Params.TryGetValue("location", out var loc) ? GetStringParam(loc) : "",
            X=N("x",-1), Y=N("y",-1), Width=N("width"), Height=N("height"),
            MinimumEnergy=Math.Max(20,N("minimum_energy",20)), StopTime=N("stop_time",2200)
        };
        if (j.Location != "Farm" || Game1.currentLocation.Name != j.Location)
            throw new InvalidOperationException("LOCATION: first move to Farm; this version works on Farm only");
        if (j.Width < 1 || j.Height < 1 || j.Width > 64 || j.Height > 64 || (long)j.Width*j.Height > 64 || j.X < 0 || j.Y < 0)
            throw new InvalidOperationException("AREA: rectangle must contain 1..64 tiles");
        var layer = Game1.currentLocation.Map.Layers[0];
        if ((long)j.X+j.Width > layer.LayerWidth || (long)j.Y+j.Height > layer.LayerHeight)
            throw new InvalidOperationException("AREA: outside map bounds");
        if (j.StopTime < 600 || j.StopTime > 2200 || j.StopTime%100 >= 60)
            throw new InvalidOperationException("stop_time must be valid HHMM from 0600 through 2200");
        return j;
    }

    private FarmTile ReadFarmTile(int x, int y)
    {
        var loc = Game1.currentLocation;
        var v = new Vector2(x,y);
        var t = new FarmTile { X=x,Y=y,Diggable=loc.doesTileHaveProperty(x,y,"Diggable","Back") != null };
        if (loc.terrainFeatures.TryGetValue(v,out var tf)) {
            t.Terrain=tf.GetType().Name;
            if (tf is HoeDirt h) { t.Hoed=true; t.Watered=h.state.Value==1; t.HasCrop=h.crop!=null;
                if(h.crop!=null) {
                    t.CropSeedId=h.crop.netSeedIndex.Value;
                    t.Dead=h.crop.dead.Value;
                    t.ReadyForHarvest=!t.Dead && h.crop.phaseDays.Count>0 && h.crop.currentPhase.Value>=h.crop.phaseDays.Count-1 && (!h.crop.fullyGrown.Value || h.crop.dayOfCurrentPhase.Value<=0);
                    t.HarvestMethod=h.crop.GetData()?.HarvestMethod.ToString() ?? "UNKNOWN";
                } }
            else if (tf is Tree tree) {
                t.IsWildTree=true;t.IsTreeStump=tree.stump.Value;t.GrowthStage=tree.growthStage.Value;
                t.TreeState=tree.stump.Value?"stump":tree.growthStage.Value>=5?"mature_tree":$"young_tree_stage_{tree.growthStage.Value}";
                t.RequiredTool="Axe";
                t.RemovalSequence=tree.stump.Value
                    ? "axe repeatedly until the terrain feature disappears"
                    : tree.growthStage.Value>=5
                        ? "axe repeatedly until the tree falls, then continue on its stump until the terrain feature disappears"
                        : "axe repeatedly until the terrain feature disappears";
                t.Obstacle=tree.stump.Value?"protected wild tree stump":"protected wild tree";
                // Ordinary farm clearing must continue to preserve trees. The dedicated
                // trees operation explicitly selects Axe without exposing trees as a
                // generally clearable obstacle.
                t.ClearTool="";t.Progress=tree.stump.Value?"stump":$"tree-stage-{tree.growthStage.Value}";
            }
            else if (tf is Grass grass) { t.Obstacle="grass"; t.ClearTool="Scythe"; t.Progress=grass.numberOfWeeds.Value.ToString(); }
            else t.Obstacle="protected terrain: "+t.Terrain;
        }
        if (loc.Objects.TryGetValue(v,out var obj)) {
            t.Obstacle="protected object: "+obj.Name; t.ClearTool="";
            t.Progress=obj.MinutesUntilReady.ToString();
            if (!obj.bigCraftable.Value && (tf == null || tf is HoeDirt)) {
                if (obj.Name.Contains("Weed")) { t.Obstacle="weed"; t.ClearTool="Scythe"; }
                else if (obj.Name.Contains("Twig")) { t.Obstacle="twig"; t.ClearTool="Axe"; }
                else if (obj.Name == "Stone") { t.Obstacle="small stone"; t.ClearTool="Pickaxe"; }
            }
        }
        if (loc.buildings.Any(b=>b.occupiesTile(v)) || loc.resourceClumps.Any(r=>r.occupiesTile(x,y)) ||
            loc.furniture.Any(f=>f.boundingBox.Value.Intersects(new Rectangle(x*64,y*64,64,64)))) {
            t.Obstacle="protected building, furniture or resource clump";t.ClearTool="";
        }
        return t;
    }

    private void RefreshFarm(FarmJob j)
    {
        j.Tiles=j.Targets.Select(p=>ReadFarmTile(p.X,p.Y)).ToList();
        j.CompletedTargets=j.Tiles.Count(t=>Satisfied(j,t));
    }

    private List<FarmApproach> ReadFarmApproaches(int targetX,int targetY,Dictionary<Point,int>? pathCache=null,bool cacheIsComplete=false)
    {
        var layer=Game1.currentLocation.Map.Layers[0];
        var result=new List<FarmApproach>();
        foreach(var item in new[]{
            (P:new Point(targetX,targetY-1),Face:"south"),
            (P:new Point(targetX-1,targetY),Face:"east"),
            (P:new Point(targetX+1,targetY),Face:"west"),
            (P:new Point(targetX,targetY+1),Face:"north")}) {
            var a=new FarmApproach { X=item.P.X,Y=item.P.Y,Face=item.Face };
            a.InBounds=a.X>=0 && a.Y>=0 && a.X<layer.LayerWidth && a.Y<layer.LayerHeight;
            if(a.InBounds) {
                int length;
                if(pathCache!=null && pathCache.TryGetValue(item.P,out length)) { }
                else if(pathCache!=null && cacheIsComplete) length=-1;
                else {
                    var path=_pathfinder.FindPath(Game1.currentLocation,Game1.player.Tile,new Vector2(a.X,a.Y));
                    length=path==null?-1:path.Count;
                    if(pathCache!=null) pathCache[item.P]=length;
                }
                a.Reachable=length>=0;a.PathLength=length;
            }
            result.Add(a);
        }
        return result;
    }

    private void AddFarmAccess(FarmTile tile,Dictionary<Point,int>? pathCache=null,bool cacheIsComplete=false)
    {
        tile.Approaches=ReadFarmApproaches(tile.X,tile.Y,pathCache,cacheIsComplete);
        tile.ReachableApproachCount=tile.Approaches.Count(a=>a.Reachable);
    }

    private CommandResponse FarmReply(GameCommand c, object data)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(data, options);
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(json)
            ?? throw new InvalidOperationException("Farm response serialization failed");
        return new CommandResponse { Id=c.Id,Success=true,Data=payload,Message="Farm task state" };
    }

    private CommandResponse FarmInspect(GameCommand c)
    {
        var j=ParseFarmArea(c);
        var tiles=new List<FarmTile>();
        var cache=_pathfinder.FindReachableTiles(Game1.currentLocation,Game1.player.Tile,256);
        for(int y=j.Y;y<j.Y+j.Height;y++) for(int x=j.X;x<j.X+j.Width;x++) {
            var tile=ReadFarmTile(x,y);AddFarmAccess(tile,cache,true);tiles.Add(tile);
        }
        var inventory=Game1.player.Items.Select((item,slot)=>new {slot,item})
            .Where(entry=>entry.item!=null)
            .Select(entry=>new { entry.slot, itemId=entry.item.ItemId, qualifiedItemId=entry.item.QualifiedItemId,
                name=entry.item.DisplayName, stack=entry.item.Stack,
                isSeed=entry.item is StardewValley.Object obj && obj.Category==StardewValley.Object.SeedsCategory }).ToList();
        return FarmReply(c,new { location=j.Location, observedAt=DateTime.UtcNow, tiles, inventory,
            energy=Game1.player.Stamina, time=Game1.timeOfDay, wateringCan=FarmWaterInfo() });
    }

    private CommandResponse FarmFindCandidates(GameCommand c)
    {
        int N(string key,int fallback) => c.Params.TryGetValue(key,out var value)?GetIntParam(value):fallback;
        string S(string key,string fallback) => c.Params.TryGetValue(key,out var value)?GetStringParam(value):fallback;
        if(Game1.currentLocation.Name!="Farm" || S("location","Farm")!="Farm")
            throw new InvalidOperationException("LOCATION: find candidates while on Farm");
        int width=N("width",0),height=N("height",0),radius=N("search_radius",8),limit=N("max_candidates",8);
        int anchorX=N("anchor_x",(int)Game1.player.Tile.X),anchorY=N("anchor_y",(int)Game1.player.Tile.Y);
        string direction=S("direction","ANY").ToUpperInvariant();
        if(width<1 || height<1 || (long)width*height>64) throw new InvalidOperationException("AREA: candidate rectangle must contain 1..64 tiles");
        if(radius<1 || radius>12 || limit<1 || limit>20) throw new InvalidOperationException("search_radius must be 1..12 and max_candidates 1..20");
        if(!new[]{"ANY","NORTH","SOUTH","EAST","WEST"}.Contains(direction)) throw new InvalidOperationException("direction must be ANY/NORTH/SOUTH/EAST/WEST");
        var layer=Game1.currentLocation.Map.Layers[0];
        var cache=_pathfinder.FindReachableTiles(Game1.currentLocation,Game1.player.Tile,Math.Min(256,radius+width+height+32));
        var candidates=new List<PlotCandidate>();
        int minX=Math.Max(0,anchorX-radius),maxX=Math.Min(layer.LayerWidth-width,anchorX+radius);
        int minY=Math.Max(0,anchorY-radius),maxY=Math.Min(layer.LayerHeight-height,anchorY+radius);
        int evaluated=0;
        for(int y=minY;y<=maxY;y++) for(int x=minX;x<=maxX;x++) {
            int centerX=x+(width-1)/2,centerY=y+(height-1)/2,dx=centerX-anchorX,dy=centerY-anchorY;
            if(direction=="NORTH" && y+height-1>=anchorY || direction=="SOUTH" && y<=anchorY || direction=="WEST" && x+width-1>=anchorX || direction=="EAST" && x<=anchorX) continue;
            evaluated++;var pc=new PlotCandidate { X=x,Y=y,Width=width,Height=height,TotalTiles=width*height,DistanceFromAnchor=Math.Abs(dx)+Math.Abs(dy) };
            for(int ty=y;ty<y+height;ty++) for(int tx=x;tx<x+width;tx++) {
                var tile=ReadFarmTile(tx,ty);
                if(!tile.Diggable) pc.Conflicts.Add($"({tx},{ty}):not_diggable");
                if(tile.HasCrop) pc.Conflicts.Add($"({tx},{ty}):crop");
                if(tile.Obstacle!="" && tile.ClearTool=="") pc.Conflicts.Add($"({tx},{ty}):{tile.Obstacle}");
                if(tile.Obstacle!="" && tile.ClearTool!="") pc.RemovableObstacles++;
                if(tile.Hoed) pc.ExistingHoeDirt++;
                var approaches=ReadFarmApproaches(tx,ty,cache,true);
                if(approaches.Any(a=>a.Reachable)) pc.AccessibleTargets++;
            }
            pc.AccessStatus=pc.AccessibleTargets==pc.TotalTiles?"READY":pc.AccessibleTargets>0?"PARTIAL":"UNREACHABLE";
            if(pc.Conflicts.Count==0 && pc.AccessStatus!="UNREACHABLE") candidates.Add(pc);
        }
        var selected=candidates.OrderByDescending(p=>p.AccessStatus=="READY").ThenByDescending(p=>p.AccessibleTargets)
            .ThenBy(p=>p.RemovableObstacles).ThenBy(p=>p.ExistingHoeDirt).ThenBy(p=>p.DistanceFromAnchor).Take(limit).ToList();
        return FarmReply(c,new { location="Farm",anchor=new { x=anchorX,y=anchorY },direction,width,height,searchRadius=radius,
            evaluatedRectangles=evaluated,candidates=selected,unexplored=false });
    }

    private CommandResponse FarmStart(GameCommand c)
    {
        string requestId=c.Params.TryGetValue("request_id",out var rid)?GetStringParam(rid):"";
        string fingerprint=JsonSerializer.Serialize(c.Params.Where(p=>p.Key!="_uiRun").OrderBy(p=>p.Key).ToDictionary(p=>p.Key,p=>p.Value));
        if(requestId!="" && _farmRequests.TryGetValue(requestId,out var prior)) {
            if(prior.Fingerprint!=fingerprint) throw new InvalidOperationException("REQUEST_ID_CONFLICT");
            return FarmReply(c,prior.Job);
        }
        if(_cheatModeEnabled || _infiniteEnergyEnabled || _timeFreezeEnabled)
            throw new InvalidOperationException("Disable existing cheat mode before verified farming");
        if (FarmActive || IsMoving || _toolUseRemaining>0 || _holdToolRemaining>0 || Game1.player.UsingTool)
            throw new InvalidOperationException("BUSY: finish current action first");
        var j=ParseFarmArea(c);
        j.Operation=c.Params.TryGetValue("operation",out var op)?GetStringParam(op):"";
        if(!new[]{"prepare","water","clear","till","plant","harvest","refill","trees","travel","collect"}.Contains(j.Operation)) throw new InvalidOperationException("Unknown farm operation");
        if(j.Operation=="collect") return FarmCollectStart(c,j,requestId,fingerprint);
        if(j.Operation=="travel") return FarmTravelStart(c,j,requestId,fingerprint);
        string filter=c.Params.TryGetValue("target_filter",out var f)?GetStringParam(f):"ALL_HOED_SOIL";
        if(filter!="ALL_HOED_SOIL" && filter!="CROPS_ONLY") throw new InvalidOperationException("Unknown target_filter");
        j.SeedItemId=c.Params.TryGetValue("seed_item_id",out var seed)?GetStringParam(seed):"";
        if(j.SeedItemId.StartsWith("(O)")) j.SeedItemId=j.SeedItemId.Substring(3);
        j.ExistingCropPolicy=c.Params.TryGetValue("existing_crop_policy",out var policy)?GetStringParam(policy):"PRESERVE_AND_REPORT";
        if(j.ExistingCropPolicy=="") j.ExistingCropPolicy="PRESERVE_AND_REPORT";
        if(!new[]{"PRESERVE_AND_REPORT","REQUIRE_SAME_CROP"}.Contains(j.ExistingCropPolicy)) throw new InvalidOperationException("Invalid crop policy");
        if(j.Operation=="plant" && j.SeedItemId=="") throw new InvalidOperationException("seed_item_id required");
        int maxTrees=c.Params.TryGetValue("max_trees",out var maxTreeValue)?GetIntParam(maxTreeValue):3;
        if(maxTrees<1 || maxTrees>12) throw new InvalidOperationException("max_trees must be 1..12");
        j.MaxTrees=maxTrees;
        if(c.Params.TryGetValue("preserve_young_trees",out var preserveYoungTrees)) {
            j.PreserveYoungTrees=preserveYoungTrees is JsonElement json
                ? json.ValueKind==JsonValueKind.True
                : Convert.ToBoolean(preserveYoungTrees);
        }
        j.ClearingPhase=j.Operation=="prepare";
        var treeTargets=new List<Point>();
        for(int y=j.Y;y<j.Y+j.Height;y++) for(int x=j.X;x<j.X+j.Width;x++) {
            var tile=ReadFarmTile(x,y);
            if(j.Operation=="trees") {
                if(tile.IsWildTree && (!j.PreserveYoungTrees || tile.IsTreeStump || tile.GrowthStage>=5)) treeTargets.Add(new Point(x,y));
                else if(tile.IsWildTree)
                    j.ExcludedTiles.Add($"({x},{y}): ordinary wild tree excluded; growth_stage={tile.GrowthStage}, stump={tile.IsTreeStump}, preserve_young_trees={j.PreserveYoungTrees}");
                else j.ExcludedTiles.Add($"({x},{y}): not an ordinary wild tree; terrain={tile.Terrain}");
                continue;
            }
            bool exclude=j.Operation=="water" && filter=="CROPS_ONLY" && !tile.HasCrop
                || j.Operation=="plant" && tile.HasCrop && j.ExistingCropPolicy=="PRESERVE_AND_REPORT"
                || j.Operation=="harvest" && !tile.ReadyForHarvest;
            if(exclude) j.ExcludedTiles.Add($"({x},{y}): excluded by operation filter; crop={tile.CropSeedId}");
            else j.Targets.Add(new Point(x,y));
        }
        if(j.Operation=="trees") {
            // A distance-only order could fill max_trees with nearby seeds and
            // saplings while leaving the mature trees the user intended to cut.
            // Prefer mature trees, then existing stumps, and use young trees
            // only for the remaining requested capacity. Distance breaks ties.
            var rankedTrees=treeTargets.Select(point=>new { Point=point,Tile=ReadFarmTile(point.X,point.Y) })
                .OrderBy(item=>item.Tile.GrowthStage>=5 && !item.Tile.IsTreeStump?0:item.Tile.IsTreeStump?1:2)
                .ThenBy(item=>Math.Abs(item.Point.X-Game1.player.Tile.X)+Math.Abs(item.Point.Y-Game1.player.Tile.Y))
                .ToList();
            j.Targets=rankedTrees.Take(j.MaxTrees).Select(item=>item.Point).ToList();
            j.TreeOrigins=j.Targets.ToList();
            if(j.TreeOrigins.Count==0) {
                for(int y=j.Y;y<j.Y+j.Height;y++) for(int x=j.X;x<j.X+j.Width;x++)
                    j.TreeOrigins.Add(new Point(x,y));
            }
            j.TreeInventoryBefore=SnapshotFarmInventory();
            foreach(var skipped in rankedTrees.Skip(j.MaxTrees))
                j.ExcludedTiles.Add($"({skipped.Point.X},{skipped.Point.Y}): max_trees limit");
            if(j.Targets.Count==0) {
                j.Status="BLOCKED";
                j.Reason=j.PreserveYoungTrees && j.ExcludedTiles.Any(item=>item.Contains("ordinary wild tree excluded"))
                    ? "NO_ELIGIBLE_TREE: observed ordinary trees are younger than growth stage 5 and preserve_young_trees=true"
                    : "NO_ELIGIBLE_TREE: requested area contains no ordinary wild tree allowed by the current policy";
            }
        }
        if(j.Operation=="refill") {
            if(j.Width!=1 || j.Height!=1) throw new InvalidOperationException("Refill requires one observed source tile");
            int slot=FarmToolSlot("Watering Can");
            if(slot<0) throw new InvalidOperationException("MISSING_TOOL: Watering Can");
            var can=(WateringCan)Game1.player.Items[slot];
            j.WaterBefore=j.WaterAfter=can.WaterLeft;
            int? capacity=FarmWaterCapacity(can);
            j.Refilled=capacity.HasValue && can.WaterLeft>=capacity.Value;
        }
        j.TotalTargets=j.Targets.Count;RefreshFarm(j);j.AlreadySatisfiedTargets=j.CompletedTargets;
        j.Deadline=DateTime.UtcNow.AddMinutes(5);j.Lease=DateTime.UtcNow.AddSeconds(20);j.StartDate=FarmDate();
        j.RequestId=requestId;
        _farmJob=j;_farmHistory[j.TaskId]=j;
        if(requestId!="") _farmRequests[requestId]=(fingerprint,j);
        SaveFarm(j);
        return FarmReply(c,j);
    }

    private CommandResponse FarmStatus(GameCommand c, bool cancel)
    {
        string id=c.Params.TryGetValue("task_id",out var v)?GetStringParam(v):"";
        if(!_farmHistory.TryGetValue(id,out var j)) throw new InvalidOperationException("Unknown task_id in this game session");
        if(j.Status=="RUNNING") {
            j.Lease=DateTime.UtcNow.AddSeconds(20);
            if(cancel) FinishFarm(j,"CANCELLED","Cancelled by caller");
        }
        return FarmReply(c,j);
    }

    private void SaveFarm(FarmJob j)
    {
        SavePendingTrees(j);
        try {
            var dir=Path.Combine(_helper.DirectoryPath,"farm-tasks");Directory.CreateDirectory(dir);
            var path=Path.Combine(dir,j.TaskId+".json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(j,new JsonSerializerOptions{WriteIndented=true}));
            File.Move(path+".tmp",path,true);
        } catch(Exception ex) { j.CheckpointError=ex.Message;_monitor.Log("[FARM CHECKPOINT] "+ex.Message,LogLevel.Warn); }
    }

    private void FinishFarm(FarmJob j,string status,string reason)
    {
        ClearMovementState();
        j.Status=status;j.Reason=reason;
        if(Context.IsWorldReady && Game1.currentLocation.Name==j.Location) RefreshFarm(j);
        SaveFarm(j);_monitor.Log($"[FARM {status}] {j.TaskId} {j.CompletedTargets}/{j.TotalTargets}: {reason}",LogLevel.Info);
    }

    private int FarmToolSlot(string name)
    {
        // Prefer the basic scythe: smallest standard arc, no universal crop harvest.
        if(name=="Scythe") {
            foreach(string id in new[]{"47","53","66"})
                for(int k=0;k<Game1.player.Items.Count;k++)
                    if(Game1.player.Items[k] is Tool tool && tool.ItemId==id && tool.Name.Contains("Scythe")) return k;
        }
        for(int i=0;i<Game1.player.Items.Count;i++) {
            var t=Game1.player.Items[i];
            if((name=="Hoe" && t is Hoe) || (name=="Watering Can" && t is WateringCan) ||
               (name=="Axe" && t is Axe) || (name=="Pickaxe" && t is Pickaxe) ||
               (name=="Scythe" && t is Tool && t.Name.Contains("Scythe"))) return i;
        }
        return -1;
    }

    private bool ScytheSafe(FarmJob j,Point stand)
    {
        // Conservative envelope; use actual crop harvest method, not translated names.
        // Unknown/modded scythes are treated as able to harvest all crops.
        int slot=FarmToolSlot("Scythe");
        if(slot<0) return false;
        string id=Game1.player.Items[slot].ItemId;
        bool allCrops=id!="47" && id!="53";
        for(int y=stand.Y-2;y<=stand.Y+2;y++) for(int x=stand.X-2;x<=stand.X+2;x++) {
            var layer=Game1.currentLocation.Map.Layers[0];
            if(x<0 || y<0 || x>=layer.LayerWidth || y>=layer.LayerHeight) return false;
            var t=ReadFarmTile(x,y);
            if(t.HasCrop) {
                if(!Game1.currentLocation.terrainFeatures.TryGetValue(new Vector2(x,y),out var feature)
                    || feature is not HoeDirt soil || soil.crop==null) return false;
                var cropData=soil.crop.GetData();
                if(allCrops || soil.crop.dead.Value || cropData==null || cropData.HarvestMethod.ToString()!="Grab") return false;
            }
            if(t.Obstacle!="" && (!InFarmArea(j,x,y) || t.ClearTool!="Scythe")) return false;
        }
        return true;
    }

    private string ChooseClearingTool(FarmJob j,FarmTile tile,Point stand)
    {
        // Terrain Grass is dispatched directly to that exact TerrainFeature in
        // ACT, so it has no scythe arc and cannot affect adjacent crops or
        // obstacles. Do not reject a grass approach using the legacy 5x5 arc
        // safety envelope.
        if(tile.Obstacle=="grass") return FarmToolSlot("Scythe")>=0 ? "Scythe" : "";
        if(tile.ClearTool!="Scythe") return tile.ClearTool;
        if(ScytheSafe(j,stand)) return "Scythe";
        // Object weeds accept a single-tile pickaxe hit. Terrain Grass does not.
        return tile.Obstacle=="weed" && !tile.HasCrop && FarmToolSlot("Pickaxe")>=0 ? "Pickaxe" : "";
    }

    private void DeferFarmTarget(FarmJob j,string reason)
    {
        j.Deferred[j.Target]=j.WorkRevision;
        string issue=$"({j.Target.X},{j.Target.Y}): {reason}";
        if(!j.Issues.Contains(issue)) j.Issues.Add(issue);
        _monitor.Log("[FARM DEFER] "+issue,LogLevel.Info);
        SaveFarm(j);j.Phase="SELECT";
    }

    private bool AimFarmTool(FarmJob j)
    {
        var player=Game1.player;
        int dx=j.Target.X-j.Stand.X,dy=j.Target.Y-j.Stand.Y;
        if(Math.Abs(dx)+Math.Abs(dy)!=1) return false;
        player.FacingDirection=dy==1?2:dy==-1?0:dx==1?1:3;
        Game1.currentCursorTile=new Vector2(j.Target.X,j.Target.Y);
        Game1.lastCursorMotionWasMouse=false;
        Game1.setMousePosition(j.Target.X*64+32-Game1.viewport.X,j.Target.Y*64+32-Game1.viewport.Y);
        return true;
    }

    public void CancelFarmOnTitle()
    {
        if(FarmActive) FinishFarm(_farmJob!,"PAUSED","RETURNED_TO_TITLE");
        _cropQuotes.Clear(); _cropReceipts.Clear(); _chestObservations.Clear();
        _shopReceipts.Clear(); _shopExits.Clear(); _shopObservation=""; _observedShop=null;
        CancelShopExit("Returned to title");
        _farmRequests.Clear();
        _farmHistory.Clear();
        _farmJob=null;
    }

    public void UpdateFarmWork()
    {
        var j=_farmJob;if(j==null || j.Status!="RUNNING") return;
        try {
            if(!Context.IsWorldReady || Game1.currentLocation.Name!=j.Location) { FinishFarm(j,"PAUSED","LOCATION_CHANGED");return; }
            var now=DateTime.UtcNow;var player=Game1.player;
            if(now>j.Lease) { FinishFarm(j,"PAUSED","CLIENT_HEARTBEAT_LOST");return; }
            if(now>j.Deadline || FarmDate()!=j.StartDate || Game1.timeOfDay>=j.StopTime) { FinishFarm(j,"PAUSED","TIME_LIMIT");return; }
            if((j.Operation=="trees" || j.Operation=="collect") && j.CollectingDrops && j.Phase.StartsWith("TREE_DROP")) {
                UpdateTreeDropCollection(j,now);return;
            }
            if(j.Phase=="WAIT_TOOL") {
                // A completed path can leave a movement direction active for another update.
                // Keep the cardinal target locked until the button press is consumed.
                if(!j.SawBusy) AimFarmTool(j);
                j.SawBusy |= player.UsingTool || !player.CanMove;
                if((now-j.PhaseStarted).TotalSeconds>5) { FinishFarm(j,"BLOCKED","TOOL_RESULT_TIMEOUT");return; }
                if(player.UsingTool || !player.CanMove) { j.IdleSince=default;return; }
                var t=ReadFarmTile(j.Target.X,j.Target.Y);
                bool success=(j.Operation=="trees" || j.Operation=="travel" || j.Operation=="collect") && j.ActionWasTree
                    ? !t.IsWildTree
                    : j.Action=="Hoe"?t.Hoed:j.Action=="Watering Can"?t.Watered:t.Obstacle=="";
                if(success) {
                    RefreshFarm(j);
                    if(j.CollectingDrops) {
                        j.ObstaclesClearedForDrops++;
                        j.DropEvidence.Add($"cleared access obstacle at ({j.Target.X},{j.Target.Y}) with {j.Action}");
                    }
                    if(j.Operation=="travel") {
                        j.ClearedTravelObstacles++;
                        j.TravelEvidence.Add($"cleared {j.TravelObstacleBefore} at ({j.Target.X},{j.Target.Y}) with {j.Action}");
                    }
                    if(j.Operation=="trees" && !j.CollectingDrops) {
                        j.CollectionOrigin=j.Target;
                        j.TreeOrigins=new List<Point>{j.Target};
                        BeginTreeDropCollection(j,now);return;
                    }
                    SaveFarm(j);j.NextActionAfter=now.AddMilliseconds(900);
                    j.Phase=j.ReturnPhase!=""?j.ReturnPhase:"SELECT";j.ReturnPhase="";return;
                }
                if(!j.SawBusy) return; // Input may not have been consumed yet.
                // CanMove may become true one or more updates before the terrain mutation is visible.
                // Give the game state a short settling window, without sending another input.
                if(j.IdleSince==default) { j.IdleSince=now;return; }
                if((now-j.IdleSince).TotalMilliseconds<500) return;
                _monitor.Log($"[FARM VERIFY] No change after settle: action={j.Action}, player=({(int)player.Tile.X},{(int)player.Tile.Y}), facing={player.FacingDirection}, expected=({j.Target.X},{j.Target.Y}), terrain={t.Terrain}, hoed={t.Hoed}, watered={t.Watered}",LogLevel.Info);
                // A normal stone/grass may need several hits. Bound attempts and recheck protections.
                if((j.Operation=="trees" || j.Operation=="travel" || j.Operation=="collect") && j.Action=="Axe" && t.IsWildTree) {
                    if(player.Stamina>=j.EnergyBeforeAction && t.Progress==j.Before) {
                        FinishFarm(j,"BLOCKED",$"NO_VERIFIED_TREE_HIT at ({t.X},{t.Y}); no blind retry");return;
                    }
                    if(t.Progress!=j.Before) j.Attempts=0;
                    else if(j.Attempts>=j.TreeHitLimit) {
                        string stage=t.IsTreeStump?"stump":t.GrowthStage>=5?"mature_tree":"young_tree";
                        FinishFarm(j,"BLOCKED",$"TREE_HIT_LIMIT {stage} at ({t.X},{t.Y}): {j.TreeHitLimit}");return;
                    }
                    // A mature tree becomes a stump before its falling animation is
                    // visually finished. Give that transition extra settling time,
                    // then continue until the original terrain feature disappears.
                    j.NextActionAfter=now.AddMilliseconds(t.IsTreeStump && j.Before!="stump"?2400:900);
                    j.Phase="ACT";return;
                }
                if(j.Action!="Hoe" && j.Action!="Watering Can" && t.ClearTool==j.Action && t.Progress!=j.Before && j.Attempts<8) { j.Phase="ACT";return; }
                FinishFarm(j,"BLOCKED",$"NO_VERIFIED_CHANGE at ({t.X},{t.Y}); no blind retry");return;
            }
            if(now<j.NextActionAfter) return;
            if(j.Operation=="travel" && j.Phase=="TRAVEL_MOVING") {
                if((now-j.PhaseStarted).TotalSeconds>20) FinishFarm(j,"BLOCKED","FINAL_MOVE_TIMEOUT");
                return;
            }
            if(Game1.activeClickableMenu!=null || player.UsingTool || !player.CanMove) return;
            if(j.Phase=="MOVING") {
                if((now-j.PhaseStarted).TotalSeconds>15) FinishFarm(j,"BLOCKED","MOVE_TIMEOUT");
                return;
            }
            if(j.Operation=="refill") {UpdateFarmRefill(j,now);return;}
            if(j.Phase=="SELECT") {
                RefreshFarm(j);
                if(j.CompletedTargets==j.TotalTargets) {
                    if(j.Operation=="trees" && !j.CollectingDrops) {BeginTreeDropCollection(j,now);return;}
                    if(j.Operation=="travel") {BeginFarmTravelFinal(j,now);return;}
                    FinishFarm(j,"COMPLETED","All eligible tiles verified");return;
                }
                if(j.ClearingPhase && !j.Tiles.Any(t=>!t.Hoed && t.Obstacle!="")) {j.ClearingPhase=false;j.Deferred.Clear();}
                var t=j.Tiles.Where(t=>!Satisfied(j,t) && (!j.ClearingPhase || t.Obstacle!="") &&
                    (!j.Deferred.TryGetValue(new Point(t.X,t.Y),out int generation) || generation<j.WorkRevision))
                    .OrderBy(t=>Math.Abs(t.X-player.Tile.X)+Math.Abs(t.Y-player.Tile.Y)).FirstOrDefault();
                if(t==null) {FinishFarm(j,"BLOCKED","Remaining targets have no safe approach; other targets processed. See issues.");return;}
                j.Target=new Point(t.X,t.Y);j.Attempts=0;
                if(j.Operation=="water" && !t.Hoed) { FinishFarm(j,"BLOCKED",$"NOT_HOED ({t.X},{t.Y})");return; }
                if((j.Operation=="prepare" || j.Operation=="till") && !t.Diggable) { FinishFarm(j,"BLOCKED",$"NOT_DIGGABLE ({t.X},{t.Y})");return; }
                if((j.Operation=="till" || j.Operation=="plant") && t.Obstacle!="" && t.ClearTool!="" && !t.HasCrop && !t.Hoed) {FinishFarm(j,"BLOCKED",$"CLEARING_REQUIRED ({t.X},{t.Y}): {t.Obstacle}");return;}
                if(j.Operation!="trees" && j.Operation!="travel" && t.Obstacle!="" && ((j.Operation!="prepare" && j.Operation!="clear") || t.ClearTool=="" || t.HasCrop || t.Hoed)) { FinishFarm(j,"BLOCKED",$"PROTECTED ({t.X},{t.Y}): {t.Obstacle}");return; }
                if(j.Operation=="plant" && !t.Hoed) {FinishFarm(j,"BLOCKED",$"NOT_HOED ({t.X},{t.Y})");return;}
                if(j.Operation=="plant" && t.HasCrop) {FinishFarm(j,"BLOCKED",$"EXISTING_CROP_CONFLICT ({t.X},{t.Y}) crop={t.CropSeedId}");return;}
                if(j.Operation=="harvest" && !t.ReadyForHarvest) {FinishFarm(j,"BLOCKED","CROP_CHANGED");return;}
                if(j.Operation=="trees" && !t.IsWildTree) {j.Phase="SELECT";return;}
                if(j.Operation=="travel" && !TravelCanClear(t)) {FinishFarm(j,"BLOCKED",$"PROTECTED_ROUTE_TILE ({t.X},{t.Y}): {t.Obstacle}");return;}
                j.Action=FarmActionCanClearTree(j,t)?"Axe":t.Obstacle!=""?t.ClearTool:(j.Operation=="prepare" || j.Operation=="till")?"Hoe":j.Operation=="plant"?"Plant":j.Operation=="harvest"?"Harvest":"Watering Can";
                j.Approaches=new List<Point>{new(t.X,t.Y-1),new(t.X-1,t.Y),new(t.X+1,t.Y),new(t.X,t.Y+1)}
                    .OrderBy(p=>InFarmArea(j,p.X,p.Y)?1:0)
                    .ThenBy(p=>Math.Abs(p.X-player.Tile.X)+Math.Abs(p.Y-player.Tile.Y)).ToList();
                j.Phase="APPROACH";
            }
            if(j.Phase=="APPROACH") {
                while(j.Approaches.Count>0) {
                    var stand=j.Approaches[0];j.Approaches.RemoveAt(0);
                    var layer=Game1.currentLocation.Map.Layers[0];
                    if(stand.X<0 || stand.Y<0 || stand.X>=layer.LayerWidth || stand.Y>=layer.LayerHeight) continue;
                    var candidate=ReadFarmTile(j.Target.X,j.Target.Y);
                    string action=FarmActionCanClearTree(j,candidate)?"Axe":candidate.Obstacle!=""?ChooseClearingTool(j,candidate,stand):j.Action;
                    if(action=="") continue;
                    if(_pathfinder.FindPath(Game1.currentLocation,player.Tile,new Vector2(stand.X,stand.Y))==null) continue;
                    j.Action=action;j.Stand=stand;j.Phase="MOVING";j.PhaseStarted=now;
                    ExecuteMoveTo(new GameCommand { Id=j.TaskId,Action="move_to",Params=new(){{"x",stand.X},{"y",stand.Y}},OnComplete=r=> {
                        if(j.Status!="RUNNING") return;
                        if(r.Success) { j.NextActionAfter=DateTime.UtcNow.AddMilliseconds(300);j.Phase="ACT"; }
                        else j.Phase="APPROACH";
                    }});
                    return;
                }
                if(j.CollectingDrops) {FinishFarm(j,"BLOCKED",$"DROP_ACCESS_OBSTACLE_UNREACHABLE at ({j.Target.X},{j.Target.Y})");return;}
                DeferFarmTarget(j,"NO_SAFE_APPROACH_OR_CLEARING_TOOL");return;
            }
            if(j.Phase=="ACT") {
                if((int)player.Tile.X!=j.Stand.X || (int)player.Tile.Y!=j.Stand.Y) { FinishFarm(j,"BLOCKED","POSITION_CHANGED");return; }
                var t=ReadFarmTile(j.Target.X,j.Target.Y);
                bool actionSatisfied=j.CollectingDrops
                    ? (j.ActionWasTree?!t.IsWildTree:t.Obstacle=="")
                    : Satisfied(j,t);
                if(actionSatisfied) {j.Phase=j.ReturnPhase!=""?j.ReturnPhase:"SELECT";j.ReturnPhase="";return;}
                if(j.Action=="Plant" || j.Action=="Harvest") {ExecuteFarmCrop(j,t,now);return;}
                if(j.Action=="Hoe" && (!t.Diggable || t.Obstacle!="" || t.HasCrop)) {FinishFarm(j,"BLOCKED","TARGET_CHANGED");return;}
                if(j.Action=="Watering Can" && (!t.Hoed || t.Obstacle!="")) {FinishFarm(j,"BLOCKED","TARGET_CHANGED");return;}
                if(j.Operation=="trees" && !j.CollectingDrops && !t.IsWildTree) {j.Phase="SELECT";return;}
                bool clearingTree=FarmActionCanClearTree(j,t);
                if(j.Action!="Hoe" && j.Action!="Watering Can" && !clearingTree && (j.Operation!="trees" || j.CollectingDrops && !t.IsWildTree)) {
                    if(t.ClearTool=="" || t.HasCrop || t.Hoed) {j.Phase="SELECT";return;}
                    string chosen=ChooseClearingTool(j,t,j.Stand);
                    if(chosen=="") {j.Phase="APPROACH";return;}
                    if(chosen!=j.Action) _monitor.Log($"[FARM TOOL] ({t.X},{t.Y}) {j.Action} -> {chosen}",LogLevel.Info);
                    j.Action=chosen;
                }
                if(j.Action!="Hoe" && j.Action!="Watering Can" && !clearingTree && (j.Operation!="trees" || j.CollectingDrops && !t.IsWildTree) && !player.Items.Any(item=>item==null)) {FinishFarm(j,"PAUSED","INVENTORY_FULL");return;}
                int slot=FarmToolSlot(j.Action);if(slot<0) {FinishFarm(j,"BLOCKED","MISSING_TOOL: "+j.Action);return;}
                if(player.Stamina < j.MinimumEnergy+4) {FinishFarm(j,"PAUSED","LOW_ENERGY");return;}
                player.CurrentToolIndex=slot;
                if(player.CurrentTool is WateringCan can && can.WaterLeft<=0) {FinishFarm(j,"PAUSED","NO_WATER");return;}
                ClearMovementState();
                if(!AimFarmTool(j)) {FinishFarm(j,"FAILED","TARGET_NOT_CARDINALLY_ADJACENT");return;}
                j.ActionWasTree=t.IsWildTree;
                if(j.Operation=="travel") j.TravelObstacleBefore=t.Obstacle;
                if(j.ActionWasTree) j.TreeHitLimit=t.IsTreeStump?5:t.GrowthStage>=5?10:4;
                j.Before=t.Progress;j.EnergyBeforeAction=player.Stamina;j.Attempts++;j.ToolUses++;j.SawBusy=false;j.IdleSince=default;j.PhaseStarted=now;j.Phase="WAIT_TOOL";
                _monitor.Log($"[FARM INPUT] action={j.Action}, player=({(int)player.Tile.X},{(int)player.Tile.Y}), facing={player.FacingDirection}, expected=({j.Target.X},{j.Target.Y})",LogLevel.Info);
                // Execute the normal equipped tool mechanics at the verified tile center.
                // This avoids losing repeated virtual Press events while retaining the
                // tool's normal energy, water, drops, upgrades and terrain rules.
                try {
                    var targetVector=new Vector2(j.Target.X,j.Target.Y);
                    // Scythes are MeleeWeapon instances. Calling DoFunction directly starts
                    // the weapon use, but the later animation frames normally perform the
                    // terrain-feature hit. Farm jobs intentionally don't run that animation,
                    // so dispatch terrain Grass through its normal tool-action hook here.
                    if(j.Action=="Scythe" &&
                        Game1.currentLocation.terrainFeatures.TryGetValue(targetVector,out var feature) &&
                        feature is Grass) {
                        bool remove=feature.performToolAction(player.CurrentTool,0,targetVector);
                        if(remove) Game1.currentLocation.terrainFeatures.Remove(targetVector);
                        _monitor.Log($"[FARM DIRECT GRASS] Scythe applied at tile=({j.Target.X},{j.Target.Y}), remove={remove}",LogLevel.Info);
                    } else {
                        player.CurrentTool.DoFunction(Game1.currentLocation,j.Target.X*64+32,j.Target.Y*64+32,0,player);
                        _monitor.Log($"[FARM DIRECT] {player.CurrentTool.Name} invoked at world=({j.Target.X*64+32},{j.Target.Y*64+32}) tile=({j.Target.X},{j.Target.Y})",LogLevel.Info);
                    }
                    j.SawBusy=true;
                } catch(Exception ex) {
                    FinishFarm(j,"BLOCKED","DIRECT_TOOL_FAILED: "+ex.Message);
                }
            }
        } catch(Exception ex) {FinishFarm(j,"FAILED",ex.Message);}
    }
}
