using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StardewMCP;

public abstract class MemoryDocument
{
    public int SchemaVersion { get; set; } = MemorySchema.CurrentVersion;
    public long Revision { get; set; }
    public string UpdatedAtUtc { get; set; } = "";
}

public sealed class NotebookDocument : MemoryDocument
{
    public List<ChestMemory> Chests { get; set; } = new();
    public List<MemoryNote> Notes { get; set; } = new();
    public Dictionary<string, string> Preferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChestMemory
{
    public string Id { get; set; } = "";
    public string Location { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
    public ChestColorMemory Color { get; set; } = new();
    public ChestPurposeMemory Purpose { get; set; } = new();
    public int Capacity { get; set; }
    public List<ChestItemMemory> Contents { get; set; } = new();
    public string ObservedGameDate { get; set; } = "";
    public int ObservedGameTime { get; set; }
    public string ObservedAtUtc { get; set; } = "";
}

public sealed class ChestColorMemory
{
    public int R { get; set; } = -1;
    public int G { get; set; } = -1;
    public int B { get; set; } = -1;
    public int A { get; set; } = -1;
}

public sealed class ChestPurposeMemory
{
    public string Value { get; set; } = "";
    public string Source { get; set; } = "unset";
    public double Confidence { get; set; }
}

public sealed class ChestItemMemory
{
    public int Slot { get; set; }
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public int Quality { get; set; }
    public int Category { get; set; }
}

public sealed class MemoryNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "note";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "user";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
}

public sealed class TaskDocument : MemoryDocument
{
    public List<PersistentTask> Tasks { get; set; } = new();
}

public sealed class PersistentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "general";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = TaskStatuses.Pending;
    public int Priority { get; set; }
    public List<TaskTarget> Targets { get; set; } = new();
    public string BlockedReason { get; set; } = "";
    public TaskResumePolicy ResumePolicy { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string CreatedGameDate { get; set; } = "";
    public int CreatedGameTime { get; set; }
    public string UpdatedGameDate { get; set; } = "";
    public int UpdatedGameTime { get; set; }
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
}

public sealed class TaskTarget
{
    public string Location { get; set; } = "";
    public int? X { get; set; }
    public int? Y { get; set; }
    public string Type { get; set; } = "";
    public string ItemId { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class TaskResumePolicy
{
    public string Mode { get; set; } = "manual";
    public string ResumeAfterGameDate { get; set; } = "";
    public int MinimumEnergy { get; set; }
    public int LatestStartTime { get; set; } = 2200;
    public Dictionary<string, string> Conditions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MemoryRestoreReport
{
    public string VerifiedAtUtc { get; set; } = "";
    public string VerificationKind { get; set; } = "";
    public string GameDate { get; set; } = "";
    public string NotebookSource { get; set; } = "";
    public string TaskSource { get; set; } = "";
    public string GoalSource { get; set; } = "";
    public long NotebookRevision { get; set; }
    public long TaskRevision { get; set; }
    public long GoalRevision { get; set; }
    public int ChestCount { get; set; }
    public int NoteCount { get; set; }
    public int OpenTaskCount { get; set; }
    public int OpenGoalCount { get; set; }
    public bool RoundTripVerified { get; set; }
    public string Error { get; set; } = "";
}

public sealed class RelevantMemoryContext
{
    public string Goal { get; set; } = "";
    public string Location { get; set; } = "";
    public string GameDate { get; set; } = "";
    public MemoryRestoreReport Restore { get; set; } = new();
    public List<RelevantChestMemory> Chests { get; set; } = new();
    public List<MemoryNote> Notes { get; set; } = new();
    public List<PersistentTask> Tasks { get; set; } = new();
    public List<LongTermGoal> Goals { get; set; } = new();
}

public sealed class RelevantChestMemory
{
    public string MemoryId { get; set; } = "";
    public string Location { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public ChestColorMemory Color { get; set; } = new();
    public ChestPurposeMemory Purpose { get; set; } = new();
    public List<ChestItemMemory> MajorContents { get; set; } = new();
    public string ObservedGameDate { get; set; } = "";
    public int ObservedGameTime { get; set; }
}

public static class MemoryContextSelector
{
    public static RelevantMemoryContext Select(NotebookDocument notebook, TaskDocument tasks, string goal, string location,
        string gameDate, MemoryRestoreReport? restore = null, int limitPerKind = 8, GoalDocument? goals = null)
    {
        notebook = MemorySchema.Normalize(notebook);
        tasks = MemorySchema.Normalize(tasks);
        limitPerKind = Math.Clamp(limitPerKind, 1, 20);
        HashSet<string> tokens = Tokenize(goal);
        bool Match(string? value) => tokens.Count > 0 && Tokenize(value ?? "").Overlaps(tokens);
        int LocationScore(string? value) => !string.IsNullOrWhiteSpace(location)
            && string.Equals(value, location, StringComparison.OrdinalIgnoreCase) ? 40 : 0;

        var selectedTasks = tasks.Tasks.Where(p => !TaskStatuses.IsTerminal(p.Status))
            .Select(p => new { Item = p, Score = 100 + p.Priority + p.Targets.Select(t => LocationScore(t.Location)).DefaultIfEmpty(0).Max()
                + (Match(p.Summary) || Match(p.Kind) || Match(p.BlockedReason) ? 30 : 0) })
            .OrderByDescending(p => p.Score).ThenByDescending(p => p.Item.UpdatedAtUtc, StringComparer.Ordinal)
            .Take(limitPerKind).Select(p => p.Item).ToList();

        var selectedChests = notebook.Chests.Select(p => new
            {
                Item = p,
                Score = LocationScore(p.Location) + (Match(p.Purpose.Value) ? 30 : 0)
                    + (p.Contents.Any(i => Match(i.Name) || Match(i.ItemId)) ? 20 : 0)
            })
            .Where(p => p.Score > 0).OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.Item.ObservedAtUtc, StringComparer.Ordinal).Take(limitPerKind)
            .Select(p => new RelevantChestMemory
            {
                MemoryId = p.Item.Id, Location = p.Item.Location, X = p.Item.TileX, Y = p.Item.TileY,
                Color = p.Item.Color, Purpose = p.Item.Purpose,
                MajorContents = p.Item.Contents.OrderByDescending(i => i.Quantity).Take(12).ToList(),
                ObservedGameDate = p.Item.ObservedGameDate, ObservedGameTime = p.Item.ObservedGameTime
            }).ToList();

        var selectedNotes = notebook.Notes.Where(p => Match(p.Text) || Match(p.Kind)
                || p.Kind.Equals("rule", StringComparison.OrdinalIgnoreCase)
                || p.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Source.Equals("user", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.UpdatedAtUtc, StringComparer.Ordinal).Take(limitPerKind).ToList();

        var selectedGoals = GoalSchema.Normalize(goals).Goals.Where(p => !GoalStatuses.IsTerminal(p.Status))
            .OrderByDescending(p => p.Status == GoalStatuses.Active || p.Status == GoalStatuses.AwaitingUser)
            .ThenByDescending(p => p.UpdatedAtUtc, StringComparer.Ordinal).Take(limitPerKind).ToList();

        return new RelevantMemoryContext
        {
            Goal = goal, Location = location, GameDate = gameDate, Restore = restore ?? new(),
            Chests = selectedChests, Notes = selectedNotes, Tasks = selectedTasks, Goals = selectedGoals
        };
    }

    private static HashSet<string> Tokenize(string value) => Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}_-]{2,}")
        .Select(p => p.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public static class TaskStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Blocked = "blocked";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        { Pending, Active, Paused, Blocked, Completed, Cancelled };

    public static bool IsTerminal(string status) =>
        status.Equals(Completed, StringComparison.OrdinalIgnoreCase)
        || status.Equals(Cancelled, StringComparison.OrdinalIgnoreCase);
}

public static class TaskResumeModes
{
    public const string Manual = "manual";
    public const string Automatic = "automatic";
    public const string NextDay = "next_day";
    public const string WhenCondition = "when_condition";
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        { Manual, Automatic, NextDay, WhenCondition };
}

public static class MemorySchema
{
    public const int CurrentVersion = 1;

    public static NotebookDocument Normalize(NotebookDocument? document)
    {
        document ??= new NotebookDocument();
        Migrate(document);
        document.Chests ??= new();
        document.Notes ??= new();
        document.Preferences = document.Preferences == null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(document.Preferences, StringComparer.OrdinalIgnoreCase);
        document.Chests = document.Chests
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Last())
            .ToList();
        foreach (ChestMemory chest in document.Chests)
        {
            chest.Contents ??= new();
            chest.Color ??= new();
            chest.Purpose ??= new();
        }
        return document;
    }

    public static TaskDocument Normalize(TaskDocument? document)
    {
        document ??= new TaskDocument();
        Migrate(document);
        document.Tasks ??= new();
        document.Tasks = document.Tasks
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Last())
            .ToList();
        foreach (PersistentTask task in document.Tasks)
        {
            task.Status = NormalizeStatus(task.Status);
            task.Targets ??= new();
            task.ResumePolicy ??= new();
            task.ResumePolicy.Mode = NormalizeResumeMode(task.ResumePolicy.Mode);
            task.ResumePolicy.Conditions ??= new(StringComparer.OrdinalIgnoreCase);
            task.Metadata ??= new(StringComparer.OrdinalIgnoreCase);
        }
        return document;
    }

    public static string NormalizeStatus(string? status)
    {
        string value = (status ?? TaskStatuses.Pending).Trim().ToLowerInvariant();
        if (!TaskStatuses.All.Contains(value))
            throw new InvalidOperationException($"Unsupported task status '{status}'.");
        return value;
    }

    public static bool CanTransition(string from, string to)
    {
        from = NormalizeStatus(from);
        to = NormalizeStatus(to);
        if (from == to) return true;
        if (TaskStatuses.IsTerminal(from)) return false;
        return to switch
        {
            TaskStatuses.Active => from is TaskStatuses.Pending or TaskStatuses.Paused or TaskStatuses.Blocked,
            TaskStatuses.Paused => from is TaskStatuses.Pending or TaskStatuses.Active or TaskStatuses.Blocked,
            TaskStatuses.Blocked => from is TaskStatuses.Pending or TaskStatuses.Active or TaskStatuses.Paused,
            TaskStatuses.Completed or TaskStatuses.Cancelled => true,
            _ => false
        };
    }

    public static string NormalizeResumeMode(string? mode)
    {
        string value = (mode ?? TaskResumeModes.Manual).Trim().ToLowerInvariant();
        if (!TaskResumeModes.All.Contains(value))
            throw new InvalidOperationException($"Unsupported task resume mode '{mode}'.");
        return value;
    }

    private static void Migrate(MemoryDocument document)
    {
        if (document.SchemaVersion < 0 || document.SchemaVersion > CurrentVersion)
            throw new InvalidOperationException($"Unsupported memory schema version {document.SchemaVersion}.");
        // Version 0 was the pre-versioned shape. All v1 additions have safe defaults.
        if (document.SchemaVersion == 0)
            document.SchemaVersion = 1;
    }
}
