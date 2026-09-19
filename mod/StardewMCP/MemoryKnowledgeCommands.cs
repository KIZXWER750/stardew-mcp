using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private CommandResponse SearchMemory(GameCommand command)
    {
        string query = ShopText(command, "query").Trim();
        string category = ShopText(command, "category").Trim().ToLowerInvariant();
        string location = ShopText(command, "location").Trim();
        string status = ShopText(command, "status").Trim();
        bool Match(string value) => query == "" || value.Contains(query, StringComparison.OrdinalIgnoreCase);
        var chests = (category == "" || category == "chest" || category == "chests")
            ? _notebook.Chests.Where(p => (location == "" || p.Location.Equals(location, StringComparison.OrdinalIgnoreCase))
                && (Match(p.Id) || Match(p.Location) || Match(p.Purpose.Value) || p.Contents.Any(i => Match(i.ItemId) || Match(i.Name))))
                .Take(50).Select(ChestMemoryResult).ToList()
            : new List<object>();
        var notes = (category == "" || category == "note" || category == "notes")
            ? _notebook.Notes.Where(p => Match(p.Text) || Match(p.Kind)).Take(50).Cast<object>().ToList()
            : new List<object>();
        var tasks = (category == "" || category == "task" || category == "tasks")
            ? _tasks.Tasks.Where(p => (status == "" || p.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
                && (location == "" || p.Targets.Any(t => t.Location.Equals(location, StringComparison.OrdinalIgnoreCase)))
                && (Match(p.Id) || Match(p.Kind) || Match(p.Summary) || Match(p.BlockedReason))).Take(50).Cast<object>().ToList()
            : new List<object>();
        return FarmReply(command, new { status = "OBSERVED", query, category, chests, notes, tasks,
            note = "Chest contents are last-observed snapshots; observedGameDate/time indicate freshness." });
    }

    private CommandResponse GetChestMemoryCommand(GameCommand command)
    {
        string id = ShopText(command, "memory_id");
        ChestMemory chest = _notebook.Chests.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown chest memory_id.");
        return FarmReply(command, new { status = "OBSERVED", chest = ChestMemoryResult(chest),
            note = "This is a last-observed snapshot, not proof of current contents." });
    }

    private CommandResponse SetChestPurposeCommand(GameCommand command)
    {
        string id = ShopText(command, "memory_id");
        string purpose = ShopText(command, "purpose").Trim();
        bool confirmed = command.Params.TryGetValue("confirmed_by_user", out object? raw) && ReadCommandBool(raw);
        if (purpose.Length is < 1 or > 500) throw new InvalidOperationException("Purpose must be 1..500 characters.");
        ChestMemory chest = SetChestPurpose(id, purpose, confirmed ? "user" : "inferred", confirmed ? 1 : 0.5);
        return FarmReply(command, new { status = "SAVED", chest = ChestMemoryResult(chest) });
    }

    private CommandResponse RememberNoteCommand(GameCommand command)
    {
        string text = ShopText(command, "text").Trim();
        string kind = ShopText(command, "kind").Trim();
        bool confirmed = command.Params.TryGetValue("confirmed_by_user", out object? raw) && ReadCommandBool(raw);
        if (text.Length is < 1 or > 1000) throw new InvalidOperationException("Note text must be 1..1000 characters.");
        if (kind.Length > 100) throw new InvalidOperationException("Note kind must be at most 100 characters.");
        MemoryNote note = UpsertMemoryNote(new MemoryNote { Kind = kind == "" ? "note" : kind, Text = text,
            Source = confirmed ? "user" : "inferred" });
        return FarmReply(command, new { status = "SAVED", note });
    }

    private CommandResponse ListPersistentTasksCommand(GameCommand command)
    {
        string status = ShopText(command, "status");
        IReadOnlyList<PersistentTask> tasks = GetPersistentTasks(status == "" ? null : status);
        return FarmReply(command, new { status = "OBSERVED", count = tasks.Count, tasks });
    }

    private CommandResponse UpsertPersistentTaskCommand(GameCommand command)
    {
        string id = ShopText(command, "task_id").Trim();
        PersistentTask task = id == "" ? new PersistentTask() : GetPersistentTasks().FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown persistent task_id.");
        if (command.Params.ContainsKey("kind")) task.Kind = ShopText(command, "kind").Trim();
        if (command.Params.ContainsKey("summary")) task.Summary = ShopText(command, "summary").Trim();
        if (command.Params.ContainsKey("status")) task.Status = ShopText(command, "status").Trim();
        if (command.Params.ContainsKey("priority")) task.Priority = ShopInt(command, "priority");
        if (command.Params.ContainsKey("resume_mode")) task.ResumePolicy.Mode = ShopText(command, "resume_mode").Trim().ToLowerInvariant();
        if (command.Params.ContainsKey("minimum_energy")) task.ResumePolicy.MinimumEnergy = ShopInt(command, "minimum_energy");
        if (command.Params.ContainsKey("latest_start_time")) task.ResumePolicy.LatestStartTime = ShopInt(command, "latest_start_time");
        string targetLocation = ShopText(command, "location").Trim();
        bool hasTarget = targetLocation != "" || command.Params.ContainsKey("x") || command.Params.ContainsKey("y") || command.Params.ContainsKey("target_type");
        if (hasTarget)
        {
            if (targetLocation == "") throw new InvalidOperationException("A structured target requires location.");
            task.Targets = new() { new TaskTarget { Location = targetLocation,
                X = command.Params.ContainsKey("x") ? ShopInt(command, "x") : null,
                Y = command.Params.ContainsKey("y") ? ShopInt(command, "y") : null,
                Type = ShopText(command, "target_type").Trim() } };
        }
        if (task.ResumePolicy.MinimumEnergy < 0 || task.ResumePolicy.LatestStartTime < 600 || task.ResumePolicy.LatestStartTime > 2600)
            throw new InvalidOperationException("Invalid task resume limits.");
        PersistentTask saved = UpsertPersistentTask(task);
        return FarmReply(command, new { status = "SAVED", task = saved });
    }

    private CommandResponse CompletePersistentTaskCommand(GameCommand command)
    {
        PersistentTask task = TransitionPersistentTask(ShopText(command, "task_id"), TaskStatuses.Completed);
        return FarmReply(command, new { status = "SAVED", task });
    }

    private CommandResponse LookupGameKnowledge(GameCommand command)
    {
        string subject = ShopText(command, "subject").Trim();
        string type = ShopText(command, "type").Trim().ToLowerInvariant();
        bool Match(string value) => subject == "" || value.Contains(subject, StringComparison.OrdinalIgnoreCase);
        var locations = (type == "" || type == "location" || type == "locations")
            ? _knowledge.Locations.Where(p => Match(p.Id)).Take(100).ToList() : new();
        var routes = (type == "" || type == "route" || type == "routes")
            ? _knowledge.Routes.Where(p => Match(p.From) || Match(p.To) || Match(p.Action)).Take(100).ToList() : new();
        var shops = (type == "" || type == "shop" || type == "shops")
            ? _knowledge.Shops.Where(p => Match(p.Id) || Match(p.Location)).Take(100).ToList() : new();
        bool explicitWikiType = type == "wiki" || _wikiKnowledgeCounts.Keys.Any(p => type == p || type == p.TrimEnd('s'));
        bool includeWiki = explicitWikiType || subject != "";
        var wiki = includeWiki ? _wikiKnowledge.Where(p =>
                (type == "" || type == "wiki" || type.TrimEnd('s') == p.Category.TrimEnd('s'))
                && (Match(p.Subject) || Match(p.SubjectId) || Match(p.Category)
                    || JsonSerializer.Serialize(p.Facts).Contains(subject, StringComparison.OrdinalIgnoreCase)))
            .Take(100).ToList() : new();
        return FarmReply(command, new { status = "OBSERVED", subject, type, contentSignature = _knowledge.ContentSignature,
            generatedAtUtc = _knowledge.GeneratedAtUtc, locations, routes, shops,
            wikiSummary = new { retrievedAtUtc = _wikiKnowledgeRetrievedAtUtc, counts = _wikiKnowledgeCounts }, wiki,
            note = "Installed game/mod content and live state outrank wiki facts. Wiki entries are attributed snapshots; use source.pageRevision and verificationStatus when judging freshness." });
    }

    private CommandResponse FindWorldRoute(GameCommand command)
    {
        string from = ShopText(command, "from").Trim();
        if (from == "") from = Game1.currentLocation.Name;
        string to = ShopText(command, "to").Trim();
        if (to == "") throw new InvalidOperationException("Route destination is required.");
        List<RouteEdgeKnowledge>? route = KnowledgeSchema.FindRoute(_knowledge, from, to);
        return FarmReply(command, new { status = route == null ? "NOT_FOUND" : "OBSERVED", from, to, route = route ?? new(),
            liveLocation = Game1.currentLocation.Name, movementPerformed = false,
            note = "Cached map connectivity only. Recheck live passability, conditions and opening hours before acting." });
    }

    private static object ChestMemoryResult(ChestMemory chest) => new
    {
        memoryId = chest.Id,
        location = chest.Location,
        x = chest.TileX,
        y = chest.TileY,
        color = chest.Color,
        purpose = chest.Purpose,
        capacity = chest.Capacity,
        contents = chest.Contents,
        observedGameDate = chest.ObservedGameDate,
        observedGameTime = chest.ObservedGameTime,
        observedAtUtc = chest.ObservedAtUtc
    };

    private static bool ReadCommandBool(object? value) => value switch
    {
        bool boolean => boolean,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        _ => false
    };
}
