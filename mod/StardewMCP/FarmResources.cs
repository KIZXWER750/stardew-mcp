using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;

namespace StardewMCP;

public partial class CommandExecutor
{
    private int? FarmWaterCapacity(WateringCan can)
    {
        // Optional version-dependent metadata; unknown capacity never means full.
        var field=can.GetType().GetField("waterCanMax",BindingFlags.Public|BindingFlags.Instance);
        return field?.GetValue(can) is int n && n>0 ? n : null;
    }
    private object FarmWaterInfo()
    {
        int slot=FarmToolSlot("Watering Can");
        if(slot<0) return new { available=false, slot=-1, waterLeft=0, capacity=(int?)null };
        var can=(WateringCan)Game1.player.Items[slot];
        return new { available=true,slot,waterLeft=can.WaterLeft,capacity=FarmWaterCapacity(can) };
    }

    private Func<int,int,bool> FarmRefillPredicate(out string evidence)
    {
        var loc=Game1.currentLocation;
        var method=loc.GetType().GetMethods(BindingFlags.Public|BindingFlags.Instance)
            .FirstOrDefault(m=>string.Equals(m.Name,"CanRefillWateringCanOnTile",StringComparison.OrdinalIgnoreCase)
                && m.ReturnType==typeof(bool) && m.GetParameters().Length==2
                && m.GetParameters().All(p=>p.ParameterType==typeof(int)));
        evidence=method!=null?"game refill predicate":"natural water candidate; refill success not yet verified";
        if(method==null) return (x,y)=>loc.isWaterTile(x,y);
        return (x,y)=>(bool)method.Invoke(loc,new object[]{x,y})!;
    }

    private CommandResponse FarmWaterSources(GameCommand c)
    {
        if(Game1.currentLocation.Name!="Farm") throw new InvalidOperationException("LOCATION: search on Farm");
        int radius=c.Params.TryGetValue("search_radius",out var r)?GetIntParam(r):32;
        int limit=c.Params.TryGetValue("max_sources",out var n)?GetIntParam(n):8;
        if(radius<1 || radius>64 || limit<1 || limit>20) throw new InvalidOperationException("Invalid search bounds");
        var predicate=FarmRefillPredicate(out string evidence);
        var reachable=_pathfinder.FindReachableTiles(Game1.currentLocation,Game1.player.Tile,256);
        var layer=Game1.currentLocation.Map.Layers[0];
        int px=(int)Game1.player.Tile.X,py=(int)Game1.player.Tile.Y;
        var candidates=new List<(int X,int Y,int Distance,List<FarmApproach> Approaches)>();
        for(int y=Math.Max(0,py-radius);y<=Math.Min(layer.LayerHeight-1,py+radius);y++)
            for(int x=Math.Max(0,px-radius);x<=Math.Min(layer.LayerWidth-1,px+radius);x++) {
                if(!predicate(x,y)) continue;
                var approaches=ReadFarmApproaches(x,y,reachable,true).Where(a=>a.Reachable).ToList();
                if(approaches.Count==0) continue;
                candidates.Add((x,y,approaches.Min(a=>a.PathLength),approaches));
            }
        var sources=candidates.OrderBy(a=>a.Distance).ThenBy(a=>a.Y).ThenBy(a=>a.X).Take(limit)
            .Select(a=>new {x=a.X,y=a.Y,pathLength=a.Distance,approaches=a.Approaches}).ToList();
        return FarmReply(c,new {location="Farm",observedAt=DateTime.UtcNow,searchRadius=radius,evidence,
            wateringCan=FarmWaterInfo(),sources,reason=sources.Count==0?"NO_REACHABLE_SOURCE_IN_SEARCH_RADIUS":""});
    }

    private void UpdateFarmRefill(FarmJob j,DateTime now)
    {
        int slot=FarmToolSlot("Watering Can");
        if(slot<0) {FinishFarm(j,"BLOCKED","MISSING_TOOL: Watering Can");return;}
        var player=Game1.player;
        var can=(WateringCan)player.Items[slot];
        j.WaterAfter=can.WaterLeft;
        if(j.Refilled) {FinishFarm(j,"COMPLETED","Can already full or refill verified");return;}
        if(j.Phase=="REFILL_VERIFY") {
            if(can.WaterLeft>j.WaterBefore) {
                j.Refilled=true;FinishFarm(j,"COMPLETED","Water increase verified");return;
            }
            if((now-j.PhaseStarted).TotalSeconds>=3) FinishFarm(j,"BLOCKED","REFILL_NO_VERIFIED_INCREASE; no retry");
            return;
        }
        var predicate=FarmRefillPredicate(out _);
        if(!predicate(j.X,j.Y)) {FinishFarm(j,"BLOCKED","SOURCE_NO_LONGER_VALID");return;}
        j.Target=new Point(j.X,j.Y);
        if(j.Phase=="SELECT") {
            j.Approaches=ReadFarmApproaches(j.X,j.Y).Where(a=>a.Reachable).OrderBy(a=>a.PathLength)
                .Select(a=>new Point(a.X,a.Y)).ToList();
            j.Phase="REFILL_APPROACH";
        }
        if(j.Phase=="REFILL_APPROACH") {
            if(j.Approaches.Count==0) {FinishFarm(j,"BLOCKED","NO_REACHABLE_SOURCE_APPROACH");return;}
            j.Stand=j.Approaches[0];j.Approaches.RemoveAt(0);
            j.Phase="MOVING";j.PhaseStarted=now;
            ExecuteMoveTo(new GameCommand {Id=j.TaskId,Action="move_to",Params=new(){{"x",j.Stand.X},{"y",j.Stand.Y}},OnComplete=response=> {
                if(j.Status!="RUNNING") return;
                j.Phase=response.Success?"REFILL_ACT":"REFILL_APPROACH";
                j.NextActionAfter=DateTime.UtcNow.AddMilliseconds(300);
            }});
            return;
        }
        if(j.Phase!="REFILL_ACT") return;
        if((int)player.Tile.X!=j.Stand.X || (int)player.Tile.Y!=j.Stand.Y) {FinishFarm(j,"BLOCKED","POSITION_CHANGED");return;}
        if(player.Stamina<j.MinimumEnergy) {FinishFarm(j,"PAUSED","LOW_ENERGY");return;}
        var capacity=FarmWaterCapacity(can);
        if(capacity.HasValue && can.WaterLeft>=capacity.Value) {j.Refilled=true;FinishFarm(j,"COMPLETED","Can already full");return;}
        ClearMovementState();player.CurrentToolIndex=slot;
        if(!AimFarmTool(j)) {FinishFarm(j,"BLOCKED","NON_CARDINAL_SOURCE");return;}
        j.WaterBefore=can.WaterLeft;j.ToolUses++;j.PhaseStarted=now;j.Phase="REFILL_VERIFY";
        // Same normal tool mechanics as other stable farming functions. Never assign WaterLeft.
        can.DoFunction(Game1.currentLocation,j.X*64+32,j.Y*64+32,0,player);
        _monitor.Log($"[FARM REFILL] task={j.TaskId}, source=({j.X},{j.Y}), before={j.WaterBefore}, after={can.WaterLeft}",StardewModdingAPI.LogLevel.Info);
    }

    private CommandResponse FarmAnalyze(GameCommand c)
    {
        var j=ParseFarmArea(c);
        string S(string key,string fallback="")=>c.Params.TryGetValue(key,out var value)?GetStringParam(value):fallback;
        j.Operation=S("operation");j.SeedItemId=S("seed_item_id");
        if(j.SeedItemId.StartsWith("(O)")) j.SeedItemId=j.SeedItemId.Substring(3);
        string filter=S("target_filter","ALL_HOED_SOIL"),policy=S("existing_crop_policy","PRESERVE_AND_REPORT");
        if(policy=="") policy="PRESERVE_AND_REPORT";
        if(!new[]{"clear","till","restore_soil","prepare","plant","water","harvest"}.Contains(j.Operation)) throw new InvalidOperationException("Unsupported analysis operation");
        if(j.Operation=="plant" && j.SeedItemId=="") throw new InvalidOperationException("seed_item_id required");
        if(!new[]{"ALL_HOED_SOIL","CROPS_ONLY"}.Contains(filter) || !new[]{"PRESERVE_AND_REPORT","REQUIRE_SAME_CROP"}.Contains(policy)) throw new InvalidOperationException("Invalid analysis filter/policy");
        var tiles=new List<object>();var needed=new HashSet<string>();
        int feasible=0,satisfied=0,blocked=0,excluded=0,seeds=0,water=0;
        var cache=_pathfinder.FindReachableTiles(Game1.currentLocation,Game1.player.Tile,256);
        for(int y=j.Y;y<j.Y+j.Height;y++) for(int x=j.X;x<j.X+j.Width;x++) {
            var t=ReadFarmTile(x,y);AddFarmAccess(t,cache,true);
            string code="READY";
            bool skip=j.Operation=="water" && filter=="CROPS_ONLY" && !t.HasCrop
                || j.Operation=="plant" && policy=="PRESERVE_AND_REPORT" && t.HasCrop
                || j.Operation=="harvest" && !t.ReadyForHarvest
                || j.Operation=="restore_soil" && (t.HasCrop || t.Obstacle!="");
            if(skip) {code="EXCLUDED";excluded++;}
            else if(Satisfied(j,t)) {code="ALREADY_SATISFIED";satisfied++;}
            else {
                if(t.Obstacle!="" && (t.ClearTool=="" || t.HasCrop || t.Hoed)) code="PROTECTED";
                else if(t.Obstacle!="" && j.Operation!="clear" && j.Operation!="prepare") code="CLEARING_REQUIRED";
                else if((j.Operation=="till" || j.Operation=="prepare") && !t.Diggable) code="NOT_DIGGABLE";
                else if((j.Operation=="water" || j.Operation=="plant") && !t.Hoed) code="NOT_HOED";
                else if(j.Operation=="plant" && t.HasCrop) code="EXISTING_CROP_CONFLICT";
                else if(t.ReachableApproachCount==0) code="NO_REACHABLE_APPROACH";
                if(code=="READY") {
                    feasible++;
                    if(j.Operation=="prepare" || j.Operation=="till") needed.Add("Hoe");
                    if(j.Operation=="restore_soil") needed.Add("Pickaxe");
                    if(t.Obstacle!="") {
                        var stand=t.Approaches.First(a=>a.Reachable);
                        string tool=ChooseClearingTool(j,t,new Point(stand.X,stand.Y));
                        if(tool!="") needed.Add(tool);
                        else {code="NO_SAFE_CLEARING_TOOL";feasible--;blocked++;}
                    }
                    if(j.Operation=="water") {needed.Add("Watering Can");water++;}
                    if(j.Operation=="plant") seeds++;
                    if(j.Operation=="harvest" && t.HarvestMethod=="Scythe") needed.Add("Scythe");
                    if(j.Operation=="harvest" && t.HarvestMethod!="Grab" && t.HarvestMethod!="Scythe") {code="UNSUPPORTED_HARVEST_METHOD";feasible--;blocked++;}
                } else blocked++;
            }
            tiles.Add(new {tile=t,analysis=code});
        }
        int availableSeeds=Game1.player.Items.OfType<StardewValley.Object>()
            .Where(i=>i.Category==StardewValley.Object.SeedsCategory && i.ItemId==j.SeedItemId).Sum(i=>i.Stack);
        int canSlot=FarmToolSlot("Watering Can");
        int availableWater=canSlot<0?0:((WateringCan)Game1.player.Items[canSlot]).WaterLeft;
        var missing=needed.Where(n=>FarmToolSlot(n)<0).ToList();
        return FarmReply(c,new {location=j.Location,operation=j.Operation,observedAt=DateTime.UtcNow,tiles,
            feasibleTargets=feasible,alreadySatisfiedTargets=satisfied,blockedTargets=blocked,excludedTargets=excluded,
            requiredTools=needed,missingTools=missing,seedsForFeasibleTargets=seeds,availableSeeds,
            dryFeasibleTargets=water,availableWater,refillNeeded=water>availableWater,
            energy=Game1.player.Stamina,minimumEnergyReserve=j.MinimumEnergy,
            enoughForNextAction=Game1.player.Stamina>=j.MinimumEnergy+4,
            time=Game1.timeOfDay,stopTime=j.StopTime,deadlineReached=Game1.timeOfDay>=j.StopTime,
            limitations="Read-only preflight, not an execution guarantee. Season/seed placement rules, total energy cost, inventory capacity and tool-grade compatibility are rechecked or may block during execution. Current routes can change."});
    }
}
