using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private WorldKnowledgeDocument _knowledge = new();
    private readonly List<WikiKnowledgeEntry> _wikiKnowledge = new();
    private readonly Dictionary<string, int> _wikiKnowledgeCounts = new(StringComparer.OrdinalIgnoreCase);
    private string _wikiKnowledgeRetrievedAtUtc = "";
    private Dictionary<string, string> _knowledgeSignatureComponents = new(StringComparer.OrdinalIgnoreCase);
    private KnowledgeCacheIndex _knowledgeCacheIndex = new();
    private string _knowledgePath = "";

    public void LoadGameKnowledge()
    {
        if (!Context.IsWorldReady) return;
        string signature = BuildContentSignature();
        _knowledgePath = Path.Combine(_helper.DirectoryPath, "data", "knowledge", signature, "world.json");
        WorldKnowledgeDocument? prior = ReadKnowledgeFile(_knowledgePath);
        _knowledge = ExtractGameKnowledge(signature, prior);
        WriteKnowledgeFile();
        WriteKnowledgeCacheIndex(signature);
        LoadWikiKnowledge();
        _monitor.Log($"[KNOWLEDGE] Extracted {_knowledge.Locations.Count} locations, {_knowledge.Routes.Count} routes and {_knowledge.Shops.Count} shops; loaded {_wikiKnowledge.Count} attributed wiki facts ({signature[..12]}).", LogLevel.Info);
    }

    public void ClearGameKnowledgeSession()
    {
        _knowledge = new();
        _wikiKnowledge.Clear();
        _wikiKnowledgeCounts.Clear();
        _wikiKnowledgeRetrievedAtUtc = "";
        _knowledgeSignatureComponents.Clear();
        _knowledgeCacheIndex = new();
        _knowledgePath = "";
    }

    private void LoadWikiKnowledge()
    {
        _wikiKnowledge.Clear();
        _wikiKnowledgeCounts.Clear();
        _wikiKnowledgeRetrievedAtUtc = "";
        string directory = Path.Combine(_helper.DirectoryPath, "data", "knowledge", "wiki", "en");
        if (!Directory.Exists(directory)) return;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (string path in Directory.GetFiles(directory, "*.json").Where(p => !Path.GetFileName(p).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                WikiKnowledgeCollection? collection = JsonSerializer.Deserialize<WikiKnowledgeCollection>(File.ReadAllText(path), options);
                if (collection == null || collection.SchemaVersion != 1) continue;
                collection.Entries ??= new();
                _wikiKnowledge.AddRange(collection.Entries.Where(p => !string.IsNullOrWhiteSpace(p.Subject)));
                _wikiKnowledgeCounts[collection.Category] = collection.Entries.Count;
                if (string.CompareOrdinal(collection.RetrievedAtUtc, _wikiKnowledgeRetrievedAtUtc) > 0)
                    _wikiKnowledgeRetrievedAtUtc = collection.RetrievedAtUtc;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[KNOWLEDGE] Could not read wiki cache {Path.GetFileName(path)}: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    private string BuildContentSignature()
    {
        var mods = _helper.ModRegistry.GetAll().Select(p => p.Manifest.UniqueID + "@" + p.Manifest.Version)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var maps = BuildMapSignatureInputs();
        var shops = BuildShopSignatureInputs();
        _knowledgeSignatureComponents = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gameVersion"] = Game1.GetVersionString(),
            ["smapiVersion"] = Constants.ApiVersion.ToString(),
            ["modsHash"] = KnowledgeSchema.ComputeContentSignature(mods),
            ["mapsHash"] = KnowledgeSchema.ComputeContentSignature(maps),
            ["shopsHash"] = KnowledgeSchema.ComputeContentSignature(shops)
        };
        return KnowledgeSchema.ComputeContentSignature(_knowledgeSignatureComponents.Select(p => p.Key + "=" + p.Value));
    }

    private static List<string> BuildMapSignatureInputs()
    {
        var parts = new List<string>();
        string[] properties = { "Action", "TouchAction", "Passable", "NPCBarrier", "NoPath", "Water", "Diggable" };
        foreach (GameLocation location in Game1.locations.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add("location=" + location.Name);
            foreach (var warp in location.warps)
                parts.Add($"warp={location.Name}|{warp.X}|{warp.Y}|{warp.TargetName}|{ReadNullableInt(warp, "TargetX")}|{ReadNullableInt(warp, "TargetY")}");
            foreach (var door in location.doors.Pairs)
                parts.Add($"door={location.Name}|{door.Key.X}|{door.Key.Y}|{door.Value}");
            if (location.Map == null) continue;
            foreach (var layer in location.Map.Layers)
            {
                parts.Add($"layer={location.Name}|{layer.Id}|{layer.LayerWidth}|{layer.LayerHeight}");
                for (int y = 0; y < layer.LayerHeight; y++)
                {
                    var row = new StringBuilder($"tiles={location.Name}|{layer.Id}|{y}|");
                    for (int x = 0; x < layer.LayerWidth; x++)
                    {
                        row.Append(layer.Tiles[x, y]?.TileIndex ?? -1).Append(',');
                        foreach (string property in properties)
                        {
                            string value = location.doesTileHaveProperty(x, y, property, layer.Id) ?? "";
                            if (value != "") row.Append(property).Append('=').Append(value).Append(';');
                        }
                    }
                    parts.Add(row.ToString());
                }
            }
        }
        return parts;
    }

    private void WriteKnowledgeCacheIndex(string signature)
    {
        try
        {
            string path = Path.Combine(_helper.DirectoryPath, "data", "knowledge", "cache-index.json");
            KnowledgeCacheIndex? previous = null;
            if (File.Exists(path)) previous = JsonSerializer.Deserialize<KnowledgeCacheIndex>(File.ReadAllText(path));
            var index = new KnowledgeCacheIndex
            {
                CurrentSignature = signature,
                PreviousSignature = previous?.CurrentSignature ?? "",
                CacheInvalidated = previous != null && !previous.CurrentSignature.Equals(signature, StringComparison.OrdinalIgnoreCase),
                VerifiedAtUtc = DateTime.UtcNow.ToString("O"), Components = new(_knowledgeSignatureComponents, StringComparer.OrdinalIgnoreCase)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            _knowledgeCacheIndex = index;
            _monitor.Log($"[KNOWLEDGE CACHE] signature={signature[..12]}, invalidated={index.CacheInvalidated}, maps={index.Components["mapsHash"][..12]}, shops={index.Components["shopsHash"][..12]}", LogLevel.Info);
        }
        catch (Exception ex) { _monitor.Log("[KNOWLEDGE CACHE] Index verification failed: " + ex.Message, LogLevel.Warn); }
    }

    private static List<string> BuildShopSignatureInputs()
    {
        var parts = new List<string>();
        try
        {
            Type? shopDataType = ResolveGameDataType("StardewValley.GameData.Shops.ShopData");
            MethodInfo? load = Game1.content.GetType().GetMethods().FirstOrDefault(p => p.Name == "Load" && p.IsGenericMethodDefinition
                && p.GetParameters().Length == 1 && p.GetParameters()[0].ParameterType == typeof(string));
            if (shopDataType == null || load == null) return parts;
            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), shopDataType);
            if (load.MakeGenericMethod(dictionaryType).Invoke(Game1.content, new object[] { "Data/Shops" }) is not IDictionary shops) return parts;
            foreach (DictionaryEntry entry in shops)
            {
                object data = entry.Value!;
                int count = ShopMember(data, "Items") is ICollection items ? items.Count : 0;
                parts.Add($"shop={entry.Key}|items={count}|open={ShopMember(data, "OpenTime")}|close={ShopMember(data, "CloseTime")}");
            }
        }
        catch { /* Extraction logs the actionable warning later; an empty hash still participates in the signature. */ }
        return parts;
    }

    private WorldKnowledgeDocument ExtractGameKnowledge(string signature, WorldKnowledgeDocument? prior)
    {
        var result = new WorldKnowledgeDocument { ContentSignature = signature, GeneratedAtUtc = DateTime.UtcNow.ToString("O") };
        ExtractShops(result);
        foreach (GameLocation location in Game1.locations)
        {
            int width = location.Map?.Layers.Count > 0 ? location.Map.Layers[0].LayerWidth : 0;
            int height = location.Map?.Layers.Count > 0 ? location.Map.Layers[0].LayerHeight : 0;
            result.Locations.Add(new LocationKnowledge { Id = location.Name, Width = width, Height = height });
            foreach (var warp in location.warps)
                result.Routes.Add(NewRoute(location, warp.TargetName, warp.X, warp.Y, "walk onto warp", "location_warp",
                    ReadNullableInt(warp, "TargetX"), ReadNullableInt(warp, "TargetY")));
            foreach (var door in location.doors.Pairs)
                result.Routes.Add(NewRoute(location, door.Value, door.Key.X, door.Key.Y, "interact with door", "location_door", null, null));
            if (width == 0 || height == 0) continue;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                foreach (string layer in new[] { "Buildings", "Back" }) foreach (string property in new[] { "Action", "TouchAction" })
                {
                    string action = location.doesTileHaveProperty(x, y, property, layer) ?? "";
                    string[] pieces = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (pieces.Length > 0 && (pieces[0] == "Shop" || pieces[0] == "SeedShop"))
                    {
                        string shopId = pieces.Length > 1 ? pieces[1] : location.Name;
                        ShopKnowledge? shop = result.Shops.FirstOrDefault(p => p.Id.Equals(shopId, StringComparison.OrdinalIgnoreCase));
                        if (shop == null)
                        {
                            shop = new ShopKnowledge { Id = shopId, Source = "map_property", Asset = "" };
                            result.Shops.Add(shop);
                        }
                        if (shop.Location == "") shop.Location = location.Name;
                        shop.Counters.Add(new ShopCounterKnowledge { Location = location.Name, X = x, Y = y, Action = action,
                            Source = "map_" + property.ToLowerInvariant() });
                    }
                    if (pieces.Length < 4 || (pieces[0] != "Warp" && pieces[0] != "LockedDoorWarp")) continue;
                    int? targetX = int.TryParse(pieces[1], out int tx) ? tx : null;
                    int? targetY = int.TryParse(pieces[2], out int ty) ? ty : null;
                    result.Routes.Add(NewRoute(location, pieces[3], x, y, action, "map_" + property.ToLowerInvariant(), targetX, targetY));
                }
            }
        }
        // These two vanilla entrances are not exposed consistently by the loaded map APIs.
        result.Routes.RemoveAll(p => p.From == "Town" && p.To == "SeedShop");
        GameLocation? town = Game1.locations.FirstOrDefault(p => p.Name == "Town");
        if (town != null) result.Routes.Add(NewRoute(town, "SeedShop", 43, 56, "Pierre fixed north door", "builtin_fallback", null, null,
            new() { new KnowledgePoint { X = 43, Y = 57 }, new KnowledgePoint { X = 44, Y = 57 } }));
        if (!result.Routes.Any(p => p.From == "Farm" && p.To == "FarmHouse"))
        {
            GameLocation? farm = Game1.locations.FirstOrDefault(p => p.Name == "Farm");
            if (farm != null) result.Routes.Add(NewRoute(farm, "FarmHouse", 64, 14, "Standard farmhouse fixed north door", "builtin_fallback", null, null,
                new() { new KnowledgePoint { X = 64, Y = 15 } }));
        }
        foreach (RouteEdgeKnowledge route in result.Routes)
        {
            RouteEdgeKnowledge? old = prior?.Routes.FirstOrDefault(p => KnowledgeSchema.RouteKey(p) == KnowledgeSchema.RouteKey(route));
            if (old?.RuntimeVerified == true)
            {
                route.RuntimeVerified = true;
                route.LastVerifiedAtUtc = old.LastVerifiedAtUtc;
            }
        }
        result.Revision = (prior?.Revision ?? 0) + 1;
        return KnowledgeSchema.Normalize(result);
    }

    private RouteEdgeKnowledge NewRoute(GameLocation from, string to, int x, int y, string action, string source,
        int? targetX, int? targetY, List<KnowledgePoint>? fixedApproaches = null)
    {
        int width = from.Map?.Layers.Count > 0 ? from.Map.Layers[0].LayerWidth : 0;
        int height = from.Map?.Layers.Count > 0 ? from.Map.Layers[0].LayerHeight : 0;
        var approaches = fixedApproaches ?? new();
        if (fixedApproaches == null)
            foreach ((int dx, int dy) in new[] { (0, 1), (-1, 0), (1, 0), (0, -1) })
                if (x + dx >= 0 && y + dy >= 0 && x + dx < width && y + dy < height)
                    approaches.Add(new KnowledgePoint { X = x + dx, Y = y + dy });
        return new RouteEdgeKnowledge { From = from.Name, To = to, X = x, Y = y, TargetX = targetX, TargetY = targetY,
            Action = action, Source = source, Approaches = approaches };
    }

    private void ExtractShops(WorldKnowledgeDocument result)
    {
        try
        {
            Type? shopDataType = ResolveGameDataType("StardewValley.GameData.Shops.ShopData");
            MethodInfo? load = Game1.content.GetType().GetMethods().FirstOrDefault(p => p.Name == "Load" && p.IsGenericMethodDefinition
                && p.GetParameters().Length == 1 && p.GetParameters()[0].ParameterType == typeof(string));
            if (shopDataType == null || load == null) throw new InvalidOperationException("ShopData loader unavailable");
            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), shopDataType);
            if (load.MakeGenericMethod(dictionaryType).Invoke(Game1.content, new object[] { "Data/Shops" }) is not IDictionary shops)
                throw new InvalidOperationException("Data/Shops did not return a dictionary");
            foreach (DictionaryEntry entry in shops)
            {
                object data = entry.Value!;
                int count = ShopMember(data, "Items") is ICollection items ? items.Count : 0;
                result.Shops.Add(new ShopKnowledge { Id = entry.Key?.ToString() ?? "", CatalogEntryCount = count });
            }
        }
        catch (Exception ex)
        {
            _monitor.Log("[KNOWLEDGE] Data/Shops extraction unavailable: " + ex.Message, LogLevel.Warn);
        }
        ShopKnowledge? pierre = result.Shops.FirstOrDefault(p => p.Id.Equals("SeedShop", StringComparison.OrdinalIgnoreCase));
        if (pierre == null)
        {
            pierre = new ShopKnowledge { Id = "SeedShop", Source = "builtin_fallback" };
            result.Shops.Add(pierre);
        }
        pierre.Location = "SeedShop";
        pierre.TradeOpens = 900;
        pierre.TradeCloses = 1700;
        pierre.ScheduleSource = "builtin_rule_with_live_validation";
        pierre.Conditions = new() { "Wednesday requires Community Center restoration or Town Key", "Most daytime festivals close trade" };
        if (!pierre.Counters.Any(p => p.Location == "SeedShop"))
            pierre.Counters.Add(new ShopCounterKnowledge { Location = "SeedShop", X = 4, Y = 18,
                Action = "Pierre sales counter", Source = "builtin_fallback" });
    }

    private static int? ReadNullableInt(object value, string member)
    {
        object? raw = ShopMember(value, member);
        return raw is int number ? number : null;
    }

    private WorldKnowledgeDocument? ReadKnowledgeFile(string path)
    {
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate)) return KnowledgeSchema.Normalize(JsonSerializer.Deserialize<WorldKnowledgeDocument>(File.ReadAllText(candidate)));
            }
            catch (Exception ex) { _monitor.Log($"[KNOWLEDGE] Could not read {Path.GetFileName(candidate)}: {ex.Message}", LogLevel.Warn); }
        }
        return null;
    }

    private void WriteKnowledgeFile()
    {
        if (_knowledgePath == "") return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_knowledgePath)!);
            string temporary = _knowledgePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_knowledge, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_knowledgePath))
            {
                try
                {
                    WorldKnowledgeDocument? validPrimary = JsonSerializer.Deserialize<WorldKnowledgeDocument>(File.ReadAllText(_knowledgePath));
                    KnowledgeSchema.Normalize(validPrimary);
                    File.Copy(_knowledgePath, _knowledgePath + ".bak", true);
                }
                catch (Exception ex)
                {
                    _monitor.Log("[KNOWLEDGE] Existing primary is invalid; retaining the previous backup: " + ex.Message, LogLevel.Warn);
                }
            }
            File.Move(temporary, _knowledgePath, true);
            WorldKnowledgeDocument? verified = ReadKnowledgeFile(_knowledgePath);
            if (verified?.Revision != _knowledge.Revision) throw new InvalidOperationException("Knowledge revision verification failed");
        }
        catch (Exception ex) { _monitor.Log("[KNOWLEDGE] Cache write failed: " + ex.Message, LogLevel.Error); }
    }

    private void MarkRouteVerified(string from, string to, int x, int y)
    {
        RouteEdgeKnowledge? route = _knowledge.Routes.FirstOrDefault(p => p.From == from && p.To == to && p.X == x && p.Y == y);
        if (route == null) return;
        route.RuntimeVerified = true;
        route.LastVerifiedAtUtc = DateTime.UtcNow.ToString("O");
        _knowledge.Revision++;
        WriteKnowledgeFile();
    }
}
