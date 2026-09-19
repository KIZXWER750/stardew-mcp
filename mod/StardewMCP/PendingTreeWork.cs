using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    public sealed class PendingTreeWork
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int MinimumEnergy { get; set; }
        public int StopTime { get; set; }
        public string Date { get; set; } = "";
        public float Energy { get; set; }
        public string Reason { get; set; } = "";
        public bool AutoResume { get; set; }
    }

    private const string TreeTaskKind = "tree_removal";

    public void LoadPendingTrees()
    {
        if (!_memoryLoaded) return;
        string path = Path.Combine(_helper.DirectoryPath, "pending-work",
            $"{Game1.uniqueIDForThisGame}-{Game1.player.UniqueMultiplayerID}-trees.json");
        if (!File.Exists(path)) return;
        try
        {
            List<PendingTreeWork> legacy = JsonSerializer.Deserialize<List<PendingTreeWork>>(File.ReadAllText(path)) ?? new();
            int imported = 0;
            foreach (PendingTreeWork old in legacy)
            {
                string id = $"legacy-tree-v1-{old.X}-{old.Y}";
                if (_tasks.Tasks.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                UpsertPersistentTask(new PersistentTask
                {
                    Id = id,
                    Kind = TreeTaskKind,
                    Summary = $"농장 ({old.X}, {old.Y})의 미완료 나무·밑동·드롭 처리",
                    Status = TaskStatuses.Paused,
                    Targets = new() { new TaskTarget { Location = "Farm", X = old.X, Y = old.Y, Type = "tree_or_stump" } },
                    BlockedReason = old.Reason,
                    ResumePolicy = new TaskResumePolicy
                    {
                        Mode = old.AutoResume ? "automatic" : "manual",
                        MinimumEnergy = old.MinimumEnergy,
                        LatestStartTime = old.StopTime,
                        ResumeAfterGameDate = old.Date
                    },
                    CreatedGameDate = old.Date,
                    Metadata = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["legacySource"] = Path.GetFileName(path),
                        ["pauseEnergy"] = old.Energy.ToString(CultureInfo.InvariantCulture),
                        ["reason"] = old.Reason
                    }
                });
                imported++;
            }
            if (imported > 0)
                _monitor.Log($"[TREE RESUME] Imported {imported} legacy checkpoints into tasks-v1; original file retained.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log("[TREE RESUME] Legacy checkpoint migration skipped: " + ex.Message, LogLevel.Error);
        }
    }

    private void SavePendingTrees(FarmJob job)
    {
        if (job.Operation != "trees" || !_memoryLoaded || !Context.IsWorldReady) return;
        var origins = job.Targets.Concat(job.TreeOrigins).Distinct().ToList();
        foreach (var point in origins)
        {
            string id = $"tree-v1-{job.TaskId}-{point.X}-{point.Y}";
            foreach (PersistentTask stale in OpenTreeTasksAt(point.X, point.Y).Where(p => p.Id != id).ToList())
                TransitionPersistentTask(stale.Id, TaskStatuses.Completed);

            bool satisfied = job.Status == "COMPLETED" || job.CollectedTreeTargets.Contains(point);
            PersistentTask? existing = _tasks.Tasks.FirstOrDefault(p => p.Id == id);
            if (satisfied)
            {
                if (existing != null && !TaskStatuses.IsTerminal(existing.Status))
                    TransitionPersistentTask(id, TaskStatuses.Completed);
                continue;
            }

            string status = job.Status switch
            {
                "RUNNING" => TaskStatuses.Active,
                "BLOCKED" => TaskStatuses.Blocked,
                "CANCELLED" => TaskStatuses.Cancelled,
                _ => TaskStatuses.Paused
            };
            bool autoResume = status == TaskStatuses.Paused
                && (job.Reason == "LOW_ENERGY" || job.Reason == "TIME_LIMIT" || job.Reason == "TIME_ALARM");
            PersistentTask task = existing ?? new PersistentTask
            {
                Id = id,
                Kind = TreeTaskKind,
                Summary = $"농장 ({point.X}, {point.Y})의 미완료 나무·밑동·드롭 처리",
                Targets = new() { new TaskTarget { Location = "Farm", X = point.X, Y = point.Y, Type = "tree_or_stump" } }
            };
            task.Status = status;
            task.BlockedReason = job.Reason;
            task.ResumePolicy = new TaskResumePolicy
            {
                Mode = autoResume ? "automatic" : "manual",
                MinimumEnergy = job.MinimumEnergy,
                LatestStartTime = job.StopTime,
                ResumeAfterGameDate = FarmDate()
            };
            task.Metadata["farmTaskId"] = job.TaskId;
            task.Metadata["pauseEnergy"] = Game1.player.Stamina.ToString(CultureInfo.InvariantCulture);
            task.Metadata["reason"] = job.Reason;
            UpsertPersistentTask(task);
        }
        if (_tasksDirty) job.CheckpointError = "Persistent task checkpoint write failed; automatic resume disabled until a later successful save.";
    }

    public void SuspendPendingTreeResume()
    {
        foreach (PersistentTask task in _tasks.Tasks.Where(p => p.Kind == TreeTaskKind && !TaskStatuses.IsTerminal(p.Status)))
            task.ResumePolicy.Mode = "manual";
        _tasksDirty = true;
        FlushLongTermMemory();
    }

    public string GetPendingTreeGoal()
    {
        if (!_memoryLoaded || _tasksDirty || !Context.IsWorldReady || Game1.timeOfDay >= 2200) return "";
        PersistentTask? task = _tasks.Tasks.FirstOrDefault(CanResumeTreeTask);
        TaskTarget? target = task?.Targets.FirstOrDefault();
        if (task == null || target?.X == null || target.Y == null) return "";
        return $"RESUME AUTHORIZED TREE WORK at Farm ({target.X},{target.Y}). This is a persisted unfinished tree/stump/drop task, not a new area. "
            + "First inspect surroundings and daily status. If safe, reach Farm using observed normal routes, then inspect the exact tile. "
            + $"Call remove_wild_trees with location=Farm,x={target.X},y={target.Y},width=1,height=1,max_trees=1,minimum_energy={task.ResumePolicy.MinimumEnergy},stop_time={task.ResumePolicy.LatestStartTime}. "
            + "The function collects remaining drops even if the original tree/stump is already gone. Preserve crops, fruit trees and facilities. "
            + "Do not choose replacement trees, eat, buy, sleep or retry an uncertain failure. Report verified progress. If conditions are unsafe, stop.";
    }

    public void MarkPendingTreeDispatched()
    {
        PersistentTask? task = _tasks.Tasks.FirstOrDefault(CanResumeTreeTask);
        if (task == null) return;
        task.ResumePolicy.Mode = "manual";
        _tasksDirty = true;
        FlushLongTermMemory();
    }

    private IEnumerable<PersistentTask> OpenTreeTasksAt(int x, int y) => _tasks.Tasks.Where(p =>
        p.Kind == TreeTaskKind && !TaskStatuses.IsTerminal(p.Status)
        && p.Targets.Any(t => t.Location == "Farm" && t.X == x && t.Y == y));

    private bool CanResumeTreeTask(PersistentTask task)
    {
        if (task.Kind != TreeTaskKind || task.ResumePolicy.Mode != "automatic" || task.Status != TaskStatuses.Paused) return false;
        float oldEnergy = 0;
        if (task.Metadata.TryGetValue("pauseEnergy", out string? text))
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out oldEnergy);
        string reason = task.Metadata.TryGetValue("reason", out string? storedReason) ? storedReason : task.BlockedReason;
        return WorkPolicy.CanResume(true, Game1.timeOfDay, task.ResumePolicy.LatestStartTime,
            Game1.player.Stamina, task.ResumePolicy.MinimumEnergy, task.ResumePolicy.ResumeAfterGameDate,
            FarmDate(), reason, oldEnergy);
    }
}
