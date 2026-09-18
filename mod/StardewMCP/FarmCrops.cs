using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewMCP;

public partial class CommandExecutor
{
    private static Dictionary<string,int> FarmInventory()
    {
        var counts=new Dictionary<string,int>();
        foreach(var item in Game1.player.Items.Where(i=>i!=null)) {
            string id=item.QualifiedItemId;
            counts[id]=counts.TryGetValue(id,out int n)?n+item.Stack:item.Stack;
        }
        return counts;
    }

    // These actions run on the game's update thread, after cardinal approach verification.
    // No Crop constructors, spawned items, maturity changes, or infinite resources.
    private void ExecuteFarmCrop(FarmJob j,FarmTile t,DateTime now)
    {
        var player=Game1.player;
        var location=Game1.currentLocation;
        var tile=new Vector2(j.Target.X,j.Target.Y);
        if(!location.terrainFeatures.TryGetValue(tile,out var feature) || feature is not HoeDirt soil || t.Obstacle!="") {
            FinishFarm(j,"BLOCKED","CROP_TARGET_CHANGED");return;
        }
        if(player.Stamina<j.MinimumEnergy+4) {FinishFarm(j,"PAUSED","LOW_ENERGY");return;}
        ClearMovementState();
        if(!AimFarmTool(j)) {FinishFarm(j,"BLOCKED","NON_CARDINAL_TARGET");return;}
        _monitor.Log($"[FARM CROP INPUT] task={j.TaskId}, action={j.Action}, player={player.Tile}, target={j.Target}", StardewModdingAPI.LogLevel.Info);

        if(j.Action=="Plant") {
            if(soil.crop!=null) {FinishFarm(j,"BLOCKED","EXISTING_CROP");return;}
            int slot=-1;
            for(int i=0;i<player.Items.Count;i++)
                if(player.Items[i] is StardewValley.Object s && s.ItemId==j.SeedItemId && s.Category==StardewValley.Object.SeedsCategory && s.Stack>0) {slot=i;break;}
            if(slot<0) {FinishFarm(j,"PAUSED","NO_SEEDS: "+j.SeedItemId);return;}
            var seed=(StardewValley.Object)player.Items[slot];
            player.CurrentToolIndex=slot;
            int before=seed.Stack;
            j.PlacementUses++;
            // Object placement checks the game's season/location/soil rules.
            bool accepted=seed.placementAction(location,j.Target.X*64+32,j.Target.Y*64+32,player);
            if(accepted) {
                // The normal placement caller consumes the inventory item. Do not double
                // consume if a modded placementAction already adjusted this same stack.
                if(ReferenceEquals(player.Items[slot],seed) && seed.Stack==before) {
                    seed.Stack--;
                    if(seed.Stack<=0) player.Items[slot]=null;
                }
                j.SeedsConsumed+=Math.Max(0,before-seed.Stack);
            }
            var after=ReadFarmTile(j.Target.X,j.Target.Y);
            if(!accepted || !after.HasCrop || after.CropSeedId!=j.SeedItemId) {
                FinishFarm(j,"BLOCKED",$"PLANT_NOT_VERIFIED accepted={accepted}, crop={after.CropSeedId}; no retry");return;
            }
        } else {
            if(soil.crop==null || !t.ReadyForHarvest) {FinishFarm(j,"BLOCKED","CROP_NOT_READY");return;}
            if(!player.Items.Any(i=>i==null)) {FinishFarm(j,"PAUSED","INVENTORY_FULL");return;}
            bool scythe=t.HarvestMethod=="Scythe";
            if(t.HarvestMethod!="Grab" && !scythe) {FinishFarm(j,"BLOCKED","UNSUPPORTED_HARVEST_METHOD: "+t.HarvestMethod);return;}
            if(scythe) {
                int slot=FarmToolSlot("Scythe");
                if(slot<0) {FinishFarm(j,"BLOCKED","MISSING_TOOL: Scythe");return;}
                player.CurrentToolIndex=slot;
            }
            var crop=soil.crop;
            var inventory=FarmInventory();
            int dropsBefore=location.debris.Count;
            // Resolve the exact supported game method before mutating anything. The optional
            // forced-scythe flag is version-dependent; missing support blocks safely.
            var method=typeof(Crop).GetMethods(BindingFlags.Public|BindingFlags.Instance)
                .Where(m=>m.Name=="harvest" && m.ReturnType==typeof(bool))
                .FirstOrDefault(m=> {
                    var ps=m.GetParameters();
                    return ps.Length>=4 && ps[0].ParameterType==typeof(int) && ps[1].ParameterType==typeof(int)
                        && ps[2].ParameterType==typeof(HoeDirt) && !ps[3].ParameterType.IsValueType
                        && ps.Skip(4).All(p=>p.HasDefaultValue)
                        && (!scythe || ps.Any(p=>p.Name=="isForcedScytheHarvest" && p.ParameterType==typeof(bool)));
                });
            if(method==null) {FinishFarm(j,"BLOCKED","UNSUPPORTED_HARVEST_API");return;}
            var parameters=method.GetParameters();
            var args=parameters.Select(p=>p.HasDefaultValue?p.DefaultValue:null).ToArray();
            args[0]=j.Target.X;args[1]=j.Target.Y;args[2]=soil;args[3]=null;
            for(int i=4;i<args.Length;i++) if(parameters[i].Name=="isForcedScytheHarvest") args[i]=scythe;
            if(scythe) j.ToolUses++; else j.InteractionUses++;
            // A single-crop normal harvest hook avoids a scythe arc hitting neighboring crops.
            // The return value indicates whether the soil should remove the harvested crop.
            bool remove=(bool)method.Invoke(crop,args)!;
            if(remove && !crop.RegrowsAfterHarvest() && ReferenceEquals(soil.crop,crop)) soil.crop=null;
            var after=ReadFarmTile(j.Target.X,j.Target.Y);
            if(after.ReadyForHarvest || (after.HasCrop && !ReferenceEquals(soil.crop,crop))) {
                FinishFarm(j,"BLOCKED","HARVEST_NOT_VERIFIED; no retry");return;
            }
            j.Harvested.Add(j.Target);
            foreach(var pair in FarmInventory()) {
                int delta=pair.Value-(inventory.TryGetValue(pair.Key,out int old)?old:0);
                if(delta>0) j.HarvestEvidence.Add($"({t.X},{t.Y}): inventory {pair.Key} +{delta}");
            }
            j.HarvestEvidence.Add($"({t.X},{t.Y}): crop transition verified; debris count delta={location.debris.Count-dropsBefore}; drops not verified collected");
        }
        RefreshFarm(j);SaveFarm(j);
        j.NextActionAfter=now.AddMilliseconds(900);j.Phase="SELECT";
    }
}
