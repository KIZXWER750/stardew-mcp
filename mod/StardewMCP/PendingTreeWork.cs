using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    public class PendingTreeWork
    {
        public int X {get;set;}
        public int Y {get;set;}
        public int MinimumEnergy {get;set;}
        public int StopTime {get;set;}
        public string Date {get;set;}="";
        public float Energy {get;set;}
        public string Reason {get;set;}="";
        public bool AutoResume {get;set;}
    }
    private List<PendingTreeWork> pendingTrees=new();
    private string pendingTreePath="";
    private bool pendingTreeWriteFailed;

    public void LoadPendingTrees()
    {
        pendingTrees=new();pendingTreeWriteFailed=false;
        pendingTreePath=Path.Combine(_helper.DirectoryPath,"pending-work",
            $"{Game1.uniqueIDForThisGame}-{Game1.player.UniqueMultiplayerID}-trees.json");
        try {
            if(File.Exists(pendingTreePath))
                pendingTrees=JsonSerializer.Deserialize<List<PendingTreeWork>>(File.ReadAllText(pendingTreePath))??new();
        } catch(Exception ex) {
            pendingTreeWriteFailed=true;
            _monitor.Log("[TREE RESUME] Could not load checkpoint; automatic resume disabled: "+ex.Message,LogLevel.Error);
        }
    }

    private void WritePendingTrees()
    {
        if(pendingTreePath=="" || pendingTreeWriteFailed) return;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(pendingTreePath)!);
            File.WriteAllText(pendingTreePath+".tmp",JsonSerializer.Serialize(pendingTrees));
            File.Move(pendingTreePath+".tmp",pendingTreePath,true);
        } catch(Exception ex) {
            pendingTreeWriteFailed=true;
            _monitor.Log("[TREE RESUME] Checkpoint write failed: "+ex.Message,LogLevel.Error);
        }
    }

    private void SavePendingTrees(FarmJob j)
    {
        if(j.Operation!="trees" || pendingTreePath=="" || !Context.IsWorldReady) return;
        var origins=j.Targets.Concat(j.TreeOrigins).Distinct().ToList();
        foreach(var point in origins) {
            pendingTrees.RemoveAll(p=>p.X==point.X && p.Y==point.Y);
            if(j.Status=="COMPLETED" || j.CollectedTreeTargets.Contains(point)) continue;
            pendingTrees.Add(new PendingTreeWork {
                X=point.X,Y=point.Y,MinimumEnergy=j.MinimumEnergy,StopTime=j.StopTime,
                Date=FarmDate(),Energy=Game1.player.Stamina,Reason=j.Reason,
                AutoResume=j.Status=="PAUSED" && (j.Reason=="LOW_ENERGY" || j.Reason=="TIME_LIMIT" || j.Reason=="TIME_ALARM")
            });
        }
        WritePendingTrees();
        if(pendingTreeWriteFailed) j.CheckpointError="Pending tree checkpoint unavailable; automatic resume disabled.";
    }

    public void SuspendPendingTreeResume()
    {
        foreach(var p in pendingTrees) p.AutoResume=false;
        WritePendingTrees();
    }

    public string GetPendingTreeGoal()
    {
        if(pendingTreeWriteFailed || !Context.IsWorldReady || Game1.timeOfDay>=2200) return "";
        var p=pendingTrees.FirstOrDefault(p=>WorkPolicy.CanResume(p.AutoResume,Game1.timeOfDay,p.StopTime,Game1.player.Stamina,p.MinimumEnergy,p.Date,FarmDate(),p.Reason,p.Energy));
        if(p==null) return "";
        return $"RESUME AUTHORIZED TREE WORK at Farm ({p.X},{p.Y}). This is a persisted unfinished tree/stump/drop task, not a new area. "
            +"First inspect surroundings and daily status. If safe, reach Farm using observed normal routes, then inspect the exact tile. "
            +$"Call remove_wild_trees with location=Farm,x={p.X},y={p.Y},width=1,height=1,max_trees=1,minimum_energy={p.MinimumEnergy},stop_time={p.StopTime}. "
            +"The function collects remaining drops even if the original tree/stump is already gone. Preserve crops, fruit trees and facilities. "
            +"Do not choose replacement trees, eat, buy, sleep or retry an uncertain failure. Report verified progress. If conditions are unsafe, stop.";
    }

    public void MarkPendingTreeDispatched()
    {
        // One AI decision per checkpoint condition. A new low-energy/time pause
        // explicitly re-arms its own exact targets; model errors never loop.
        var p=pendingTrees.FirstOrDefault(p=>WorkPolicy.CanResume(p.AutoResume,Game1.timeOfDay,p.StopTime,Game1.player.Stamina,p.MinimumEnergy,p.Date,FarmDate(),p.Reason,p.Energy));
        if(p!=null) {p.AutoResume=false;WritePendingTrees();}
    }
}
