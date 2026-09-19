using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewMCP;

public sealed class WorldKnowledgeDocument
{
    public int SchemaVersion { get; set; } = KnowledgeSchema.CurrentVersion;
    public long Revision { get; set; }
    public string ContentSignature { get; set; } = "";
    public string GeneratedAtUtc { get; set; } = "";
    public List<LocationKnowledge> Locations { get; set; } = new();
    public List<RouteEdgeKnowledge> Routes { get; set; } = new();
    public List<ShopKnowledge> Shops { get; set; } = new();
}

public sealed class LocationKnowledge
{
    public string Id { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Source { get; set; } = "loaded_map";
}

public sealed class RouteEdgeKnowledge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int? TargetX { get; set; }
    public int? TargetY { get; set; }
    public string Action { get; set; } = "";
    public string Source { get; set; } = "";
    public bool RuntimeVerified { get; set; }
    public string LastVerifiedAtUtc { get; set; } = "";
    public List<KnowledgePoint> Approaches { get; set; } = new();
}

public sealed class KnowledgePoint
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class ShopKnowledge
{
    public string Id { get; set; } = "";
    public string Location { get; set; } = "";
    public int? TradeOpens { get; set; }
    public int? TradeCloses { get; set; }
    public int CatalogEntryCount { get; set; }
    public List<string> Conditions { get; set; } = new();
    public List<ShopCounterKnowledge> Counters { get; set; } = new();
    public string Source { get; set; } = "game_asset";
    public string Asset { get; set; } = "Data/Shops";
    public string ScheduleSource { get; set; } = "unknown";
}

public sealed class ShopCounterKnowledge
{
    public string Location { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Action { get; set; } = "";
    public string Source { get; set; } = "map_action";
}

public static class KnowledgeSchema
{
    public const int CurrentVersion = 1;

    public static WorldKnowledgeDocument Normalize(WorldKnowledgeDocument? document)
    {
        document ??= new();
        if (document.SchemaVersion < 0 || document.SchemaVersion > CurrentVersion)
            throw new InvalidOperationException($"Unsupported knowledge schema version {document.SchemaVersion}.");
        if (document.SchemaVersion == 0) document.SchemaVersion = 1;
        document.Locations ??= new();
        document.Routes ??= new();
        document.Shops ??= new();
        document.Locations = document.Locations.Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(p => p.Last()).ToList();
        document.Routes = document.Routes.Where(p => !string.IsNullOrWhiteSpace(p.From) && !string.IsNullOrWhiteSpace(p.To))
            .GroupBy(RouteKey, StringComparer.OrdinalIgnoreCase).Select(p => p.Last()).ToList();
        document.Shops = document.Shops.Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(p => p.Last()).ToList();
        foreach (RouteEdgeKnowledge edge in document.Routes) edge.Approaches ??= new();
        foreach (ShopKnowledge shop in document.Shops)
        {
            shop.Conditions ??= new();
            shop.Counters ??= new();
            shop.Counters = shop.Counters.GroupBy(p => $"{p.Location}|{p.X}|{p.Y}", StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Last()).ToList();
        }
        return document;
    }

    public static List<RouteEdgeKnowledge>? FindRoute(WorldKnowledgeDocument document, string from, string to)
    {
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) return new();
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        var parents = new Dictionary<string, RouteEdgeKnowledge>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            foreach (RouteEdgeKnowledge edge in document.Routes.Where(p => p.From.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                if (!seen.Add(edge.To)) continue;
                parents[edge.To] = edge;
                if (edge.To.Equals(to, StringComparison.OrdinalIgnoreCase))
                {
                    var result = new List<RouteEdgeKnowledge>();
                    string cursor = to;
                    while (parents.TryGetValue(cursor, out RouteEdgeKnowledge? step))
                    {
                        result.Insert(0, step);
                        cursor = step.From;
                    }
                    return result;
                }
                queue.Enqueue(edge.To);
            }
        }
        return null;
    }

    public static string RouteKey(RouteEdgeKnowledge edge) => $"{edge.From}|{edge.To}|{edge.X}|{edge.Y}|{edge.Action}";
}
