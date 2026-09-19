using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using xTile.Dimensions;
using xTile.Layers;
using xTile.Tiles;
using SObject = StardewValley.Object;

namespace StardewMCP;

/// <summary>Serializes game state to JSON for WebSocket transmission.</summary>
public class GameStateSerializer
{
    private const int ScanRadius = 30; // Tiles to scan around player (matches agent's 61x61 vision)
    private CommandExecutor? _commandExecutor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Set the command executor for movement state tracking.</summary>
    public void SetCommandExecutor(CommandExecutor executor)
    {
        _commandExecutor = executor;
    }

    /// <summary>Get the current game state as JSON.</summary>
    public string GetGameStateJson()
    {
        try
        {
            var state = GetGameState();
            return JsonSerializer.Serialize(state, JsonOptions);
        }
        catch
        {
            // Return minimal state on error to prevent crashes
            return JsonSerializer.Serialize(new GameState(), JsonOptions);
        }
    }

    /// <summary>Get the current game state.</summary>
    public GameState GetGameState()
    {
        var player = Game1.player;
        var location = Game1.currentLocation;

        if (player == null || location == null)
            return new GameState();

        int playerX = (int)player.Tile.X;
        int playerY = (int)player.Tile.Y;

        return new GameState
        {
            Player = GetPlayerState(player, location),
            Time = GetTimeState(),
            World = GetWorldState(location),
            Surroundings = GetSurroundings(location, playerX, playerY),
            Map = GetMapInfo(location),
            Quests = GetActiveQuests(player),
            Relationships = GetRelationships(player),
            Skills = GetSkills(player)
        };
    }

    private List<QuestInfo> GetActiveQuests(Farmer player)
    {
        var quests = new List<QuestInfo>();
        try
        {
            foreach (var quest in player.questLog)
            {
                quests.Add(new QuestInfo
                {
                    Id = quest.id.Value,
                    Name = quest.questTitle,
                    Description = quest.questDescription,
                    Objective = quest.currentObjective,
                    IsComplete = quest.completed.Value,
                    DaysLeft = quest.daysLeft.Value,
                    Reward = quest.moneyReward.Value
                });
            }
        }
        catch { /* Ignore errors */ }
        return quests;
    }

    private List<RelationshipInfo> GetRelationships(Farmer player)
    {
        var relationships = new List<RelationshipInfo>();
        try
        {
            foreach (var kvp in player.friendshipData.Pairs)
            {
                var friendship = kvp.Value;
                relationships.Add(new RelationshipInfo
                {
                    NpcName = kvp.Key,
                    FriendshipPoints = friendship.Points,
                    Hearts = friendship.Points / 250,
                    GiftsToday = friendship.GiftsToday,
                    GiftsThisWeek = friendship.GiftsThisWeek,
                    TalkedToToday = friendship.TalkedToToday,
                    Status = friendship.Status.ToString()
                });
            }
        }
        catch { /* Ignore errors */ }
        return relationships.OrderByDescending(r => r.Hearts).ToList();
    }

    private SkillsInfo GetSkills(Farmer player)
    {
        return new SkillsInfo
        {
            Farming = player.FarmingLevel,
            Mining = player.MiningLevel,
            Foraging = player.ForagingLevel,
            Fishing = player.FishingLevel,
            Combat = player.CombatLevel,
            FarmingXp = player.experiencePoints[0],
            MiningXp = player.experiencePoints[3],
            ForagingXp = player.experiencePoints[2],
            FishingXp = player.experiencePoints[1],
            CombatXp = player.experiencePoints[4]
        };
    }

    private PlayerState GetPlayerState(Farmer player, GameLocation location)
    {
        var state = new PlayerState
        {
            Name = player.Name,
            X = (int)player.Tile.X,
            Y = (int)player.Tile.Y,
            Location = location?.Name ?? "Unknown",
            Energy = player.Stamina,
            MaxEnergy = player.MaxStamina,
            Health = player.health,
            MaxHealth = player.maxHealth,
            Money = player.Money,
            CurrentTool = player.CurrentTool?.Name ?? "None",
            CurrentToolIndex = player.CurrentToolIndex,
            FacingDirection = player.FacingDirection,
            FacingDirectionName = GetDirectionName(player.FacingDirection),
            IsMoving = player.isMoving(),
            CanMove = Game1.player.CanMove,
            ShopOpen = Game1.activeClickableMenu is StardewValley.Menus.ShopMenu,
            Inventory = GetInventory(player)
        };

        // Add pathfinding state from CommandExecutor
        if (_commandExecutor != null)
        {
            state.IsPathfinding = _commandExecutor.IsMoving;
            if (_commandExecutor.MovementTarget.HasValue)
            {
                state.PathfindingTargetX = (int)_commandExecutor.MovementTarget.Value.X;
                state.PathfindingTargetY = (int)_commandExecutor.MovementTarget.Value.Y;
            }
            state.PathProgress = _commandExecutor.PathProgress;
            state.PathLength = _commandExecutor.PathLength;
        }

        return state;
    }

    private TimeState GetTimeState()
    {
        return new TimeState
        {
            TimeOfDay = Game1.timeOfDay,
            TimeString = FormatTime(Game1.timeOfDay),
            Day = Game1.dayOfMonth,
            Season = Game1.currentSeason,
            Year = Game1.year,
            DayOfWeek = GetDayOfWeek(Game1.dayOfMonth),
            IsNight = Game1.currentLocation != null && Game1.isDarkOut(Game1.currentLocation),
            MinutesUntilMorning = GetMinutesUntilMorning()
        };
    }

    private WorldState GetWorldState(GameLocation location)
    {
        return new WorldState
        {
            Weather = GetWeather(),
            IsOutdoors = location?.IsOutdoors ?? false,
            IsFarm = location?.IsFarm ?? false,
            IsGreenhouse = location?.IsGreenhouse ?? false,
            IsBuildableLocation = location?.IsBuildableLocation() ?? false,
            LocationType = GetLocationType(location)
        };
    }

    private SurroundingsState GetSurroundings(GameLocation location, int playerX, int playerY)
    {
        if (location == null)
            return new SurroundingsState();

        return new SurroundingsState
        {
            FurnitureInfo = string.Join("\n", location.furniture.Select(f =>
                $"{f.Name}: class={f.GetType().Name}, origin=({(int)f.TileLocation.X},{(int)f.TileLocation.Y}), boundsPixels=({f.boundingBox.Value.X},{f.boundingBox.Value.Y},{f.boundingBox.Value.Width},{f.boundingBox.Value.Height})")),
            AsciiMap = GenerateAsciiMap(location, playerX, playerY),
            NearbyTiles = GetNearbyTiles(location, playerX, playerY),
            NearbyObjects = GetNearbyObjects(location, playerX, playerY),
            NearbyTerrainFeatures = GetNearbyTerrainFeatures(location, playerX, playerY),
            NearbyNPCs = GetNearbyNPCs(location, playerX, playerY),
            NearbyMonsters = GetNearbyMonsters(location, playerX, playerY),
            NearbyResourceClumps = GetNearbyResourceClumps(location, playerX, playerY),
            NearbyDebris = GetNearbyDebris(location, playerX, playerY),
            NearbyBuildings = GetNearbyBuildings(location, playerX, playerY),
            NearbyAnimals = GetNearbyAnimals(location, playerX, playerY),
            WarpPoints = GetWarpPoints(location),
            TileInFront = GetTileInFront(location, playerX, playerY, Game1.player.FacingDirection)
        };
    }

    /// <summary>Generate an ASCII map of the surroundings for AI visualization.</summary>
    /// <remarks>
    /// Legend:
    /// @ = Player, . = Ground, # = Wall/Building, ~ = Water
    /// T = Tree, O = Object/Stone/Debris, C = Crop, H = Hoe Dirt
    /// " = Grass, > = Warp/Door, ; = Artifact Spot, ! = NPC, M = Monster
    /// </remarks>
    private string GenerateAsciiMap(GameLocation location, int centerX, int centerY)
    {
        if (location?.Map == null)
            return "";

        var map = location.Map;
        var layer = map.Layers.FirstOrDefault();
        if (layer == null)
            return "";

        int mapSize = ScanRadius * 2 + 1; // 61x61 grid
        var grid = new char[mapSize, mapSize];

        // Initialize with ground
        for (int y = 0; y < mapSize; y++)
            for (int x = 0; x < mapSize; x++)
                grid[y, x] = '.';

        // Fill in the map data
        for (int dy = -ScanRadius; dy <= ScanRadius; dy++)
        {
            for (int dx = -ScanRadius; dx <= ScanRadius; dx++)
            {
                int worldX = centerX + dx;
                int worldY = centerY + dy;
                int gridX = dx + ScanRadius;
                int gridY = dy + ScanRadius;

                // Skip if out of map bounds
                if (worldX < 0 || worldY < 0 || worldX >= layer.LayerWidth || worldY >= layer.LayerHeight)
                {
                    grid[gridY, gridX] = '#'; // Out of bounds = wall
                    continue;
                }

                var tileVector = new Vector2(worldX, worldY);
                var tileLocation = new Location(worldX, worldY);
                char tileChar = '.';

                // Check water first
                if (location.isWaterTile(worldX, worldY))
                {
                    tileChar = '~';
                }
                // Check passability of base tile
                else if (!location.isTilePassable(tileLocation, Game1.viewport))
                {
                    tileChar = '#';
                }

                // Check for warps/doors
                bool isWarp = location.warps.Any(w => w.X == worldX && w.Y == worldY) ||
                             location.doors.ContainsKey(new Microsoft.Xna.Framework.Point(worldX, worldY));
                if (isWarp)
                {
                    tileChar = '>';
                }

                // Check terrain features (trees, crops, grass)
                if (location.terrainFeatures.TryGetValue(tileVector, out var feature))
                {
                    tileChar = feature switch
                    {
                        Tree => 'T',
                        FruitTree => 'T',
                        HoeDirt hoeDirt when hoeDirt.crop != null => 'C',
                        HoeDirt => 'H',
                        Grass => '"',
                        Bush => 'T',
                        _ => feature.isPassable() ? tileChar : 'O'
                    };
                }

                // Check objects (stones, debris, machines, etc)
                if (location.Objects.TryGetValue(tileVector, out var obj))
                {
                    if (obj.Name.Contains("Artifact Spot"))
                        tileChar = ';';
                    else if (!obj.isPassable())
                        tileChar = 'O';
                }

                // Check resource clumps (large stumps, boulders, meteorites)
                foreach (var clump in location.resourceClumps)
                {
                    if (clump.occupiesTile(worldX, worldY))
                    {
                        tileChar = 'O';
                        break;
                    }
                }

                // Check buildings
                foreach (var building in location.buildings)
                {
                    if (building.occupiesTile(tileVector))
                    {
                        // Check if this is the door tile
                        int doorX = building.tileX.Value + building.humanDoor.X;
                        int doorY = building.tileY.Value + building.humanDoor.Y;
                        tileChar = (worldX == doorX && worldY == doorY) ? '>' : '#';
                        break;
                    }
                }

                // Check furniture
                foreach (var furn in location.furniture)
                {
                    if (furn.TileLocation == tileVector ||
                        furn.boundingBox.Value.Contains(worldX * 64 + 32, worldY * 64 + 32))
                    {
                        tileChar = '#';
                        break;
                    }
                }

                grid[gridY, gridX] = tileChar;
            }
        }

        // Overlay NPCs
        try
        {
            foreach (var npc in location.characters.ToList())
            {
                int npcX = (int)npc.Tile.X;
                int npcY = (int)npc.Tile.Y;
                int gridX = npcX - centerX + ScanRadius;
                int gridY = npcY - centerY + ScanRadius;

                if (gridX >= 0 && gridX < mapSize && gridY >= 0 && gridY < mapSize)
                {
                    grid[gridY, gridX] = npc is Monster ? 'M' : '!';
                }
            }
        }
        catch { /* Ignore concurrent modification */ }

        // Place player marker
        grid[ScanRadius, ScanRadius] = '@';

        // Convert to string
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < mapSize; y++)
        {
            for (int x = 0; x < mapSize; x++)
            {
                sb.Append(grid[y, x]);
            }
            if (y < mapSize - 1)
                sb.AppendLine();
        }

        return sb.ToString();
    }

    private MapInfo GetMapInfo(GameLocation location)
    {
        if (location?.Map == null)
            return new MapInfo();

        var map = location.Map;
        var layer = map.Layers.FirstOrDefault();

        return new MapInfo
        {
            Name = location.Name,
            DisplayName = location.DisplayName ?? location.Name,
            Width = layer?.LayerWidth ?? 0,
            Height = layer?.LayerHeight ?? 0,
            IsMineLevel = location is MineShaft,
            MineLevel = location is MineShaft mine ? mine.mineLevel : 0,
            UniqueId = location.NameOrUniqueName
        };
    }

    private List<TileInfo> GetNearbyTiles(GameLocation location, int centerX, int centerY)
    {
        var tiles = new List<TileInfo>();
        var map = location.Map;
        if (map == null) return tiles;

        var layer = map.Layers.FirstOrDefault();
        if (layer == null) return tiles;

        for (int x = centerX - ScanRadius; x <= centerX + ScanRadius; x++)
        {
            for (int y = centerY - ScanRadius; y <= centerY + ScanRadius; y++)
            {
                if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight)
                    continue;

                var tileLocation = new Location(x, y);
                var tileVector = new Vector2(x, y);

                bool isPassable = location.isTilePassable(tileLocation, Game1.viewport);
                bool isWater = location.isWaterTile(x, y);
                bool hasObject = location.Objects.ContainsKey(tileVector);
                bool hasTerrain = location.terrainFeatures.ContainsKey(tileVector);

                // Only include tiles with something notable or that block movement
                if (!isPassable || isWater || hasObject || hasTerrain ||
                    IsTileOccupied(location, x, y))
                {
                    tiles.Add(new TileInfo
                    {
                        X = x,
                        Y = y,
                        IsPassable = isPassable && !IsTileOccupied(location, x, y),
                        IsWater = isWater,
                        IsTillable = location.doesTileHaveProperty(x, y, "Diggable", "Back") != null,
                        HasObject = hasObject,
                        HasTerrainFeature = hasTerrain,
                        TileType = GetTileType(location, x, y)
                    });
                }
            }
        }

        return tiles;
    }

    private bool IsTileOccupied(GameLocation location, int x, int y)
    {
        try
        {
            var tileVector = new Vector2(x, y);

            // Check objects - use TryGetValue for thread safety
            if (location.Objects.TryGetValue(tileVector, out var obj))
            {
                if (!obj.isPassable())
                    return true;
            }

            // Check terrain features - use TryGetValue for thread safety
            if (location.terrainFeatures.TryGetValue(tileVector, out var feature))
            {
                if (!feature.isPassable())
                    return true;
            }

            // Check resource clumps - create snapshot
            var clumps = location.resourceClumps.ToList();
            foreach (var clump in clumps)
            {
                if (clump.occupiesTile(x, y))
                    return true;
            }

            // Check buildings - create snapshot
            var buildings = location.buildings.ToList();
            foreach (var building in buildings)
            {
                if (building.occupiesTile(tileVector))
                    return true;
            }

            // Check furniture - create snapshot
            var furniture = location.furniture.ToList();
            foreach (var furn in furniture)
            {
                if (furn.TileLocation == tileVector ||
                    furn.boundingBox.Value.Contains(x * 64 + 32, y * 64 + 32))
                    return true;
            }

            return false;
        }
        catch
        {
            // On concurrent modification, assume occupied for safety
            return true;
        }
    }

    private string GetTileType(GameLocation location, int x, int y)
    {
        if (location.isWaterTile(x, y)) return "water";
        if (location.doesTileHaveProperty(x, y, "Diggable", "Back") != null) return "tillable";
        if (location.doesTileHaveProperty(x, y, "NoFurniture", "Back") != null) return "no_furniture";
        if (location.doesTileHaveProperty(x, y, "Buildable", "Back") != null) return "buildable";
        return "normal";
    }

    private List<NearbyObject> GetNearbyObjects(GameLocation location, int centerX, int centerY)
    {
        var objects = new List<NearbyObject>();

        try
        {
            // Create snapshot to avoid concurrent modification
            var objectPairs = location.Objects.Pairs.ToList();

            foreach (var kvp in objectPairs)
            {
                var pos = kvp.Key;
                var obj = kvp.Value;

                if (Math.Abs(pos.X - centerX) <= ScanRadius && Math.Abs(pos.Y - centerY) <= ScanRadius)
                {
                    objects.Add(new NearbyObject
                    {
                        X = (int)pos.X,
                        Y = (int)pos.Y,
                        Name = obj.Name,
                        DisplayName = obj.DisplayName,
                        Type = GetObjectType(obj),
                        IsPassable = obj.isPassable(),
                        CanBePickedUp = obj.CanBeGrabbed,
                        IsReadyForHarvest = obj.readyForHarvest.Value,
                        MinutesUntilReady = obj.MinutesUntilReady,
                        HeldItemName = obj.heldObject.Value?.Name,
                        RequiredTool = GetRequiredToolForObject(obj),
                        HitsRequired = GetHitsRequiredForObject(obj)
                    });
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return objects;
    }

    private string GetObjectType(SObject obj)
    {
        if (obj is Chest) return "chest";
        if (obj is Fence) return "fence";
        if (obj is CrabPot) return "crab_pot";
        if (obj is IndoorPot) return "indoor_pot";
        if (obj.IsSprinkler()) return "sprinkler";
        if (obj.bigCraftable.Value) return "machine";
        if (obj.Name.Contains("Twig")) return "twig";
        if (obj.Name.Contains("Weed")) return "weed";
        if (obj.Name.Contains("Stone")) return "stone";
        if (obj.Type == "Crafting") return "crafting";
        return "object";
    }

    /// <summary>Determine what tool is needed to break/interact with an object.</summary>
    private string? GetRequiredToolForObject(SObject obj)
    {
        // Weeds - scythe (0 energy)
        if (obj.Name.Contains("Weed"))
            return "Scythe";

        // Stones - pickaxe
        if (obj.Name.Contains("Stone"))
            return "Pickaxe";

        // Twigs - axe
        if (obj.Name.Contains("Twig"))
            return "Axe";

        // Artifact spots - hoe
        if (obj.Name.Contains("Artifact Spot"))
            return "Hoe";

        // Forage items - no tool needed, just pick up
        if (obj.isForage() || obj.CanBeGrabbed)
            return null;

        return null;
    }

    /// <summary>Determine what tool is needed for a terrain feature.</summary>
    private string? GetRequiredToolForTerrain(TerrainFeature feature)
    {
        return feature switch
        {
            Tree tree when tree.growthStage.Value >= 5 => "Axe",
            Tree tree when tree.growthStage.Value < 5 => "Axe", // Saplings too
            FruitTree => "Axe",
            Grass => "Scythe",
            Bush => "Axe",
            HoeDirt hoeDirt when CropReady(hoeDirt.crop) && hoeDirt.crop?.GetData()?.HarvestMethod.ToString() == "Scythe" => "Scythe",
            _ => null
        };
    }

    /// <summary>Get player's axe upgrade level (0=basic, 1=copper, 2=steel, 3=gold, 4=iridium).</summary>
    private int GetAxeLevel()
    {
        var axe = Game1.player.Items.FirstOrDefault(i => i is StardewValley.Tools.Axe) as StardewValley.Tools.Axe;
        return axe?.UpgradeLevel ?? 0;
    }

    /// <summary>Get player's pickaxe upgrade level.</summary>
    private int GetPickaxeLevel()
    {
        var pick = Game1.player.Items.FirstOrDefault(i => i is StardewValley.Tools.Pickaxe) as StardewValley.Tools.Pickaxe;
        return pick?.UpgradeLevel ?? 0;
    }

    /// <summary>Calculate hits required for an object based on player's tool level.</summary>
    private int GetHitsRequiredForObject(SObject obj)
    {
        // Weeds, twigs: always 1 hit
        if (obj.Name.Contains("Weed")) return 1;
        if (obj.Name.Contains("Twig")) return 1;

        // Artifact spots: 1 hit with hoe
        if (obj.Name.Contains("Artifact Spot")) return 1;

        // Stones on farm: typically 1 hit
        if (obj.Name.Contains("Stone"))
        {
            // Farm stones are usually 1 hit, mine stones vary
            return 1;
        }

        // Forage: 0 hits (just pick up)
        if (obj.isForage() || obj.CanBeGrabbed) return 0;

        return 1;
    }

    /// <summary>Calculate hits required for a terrain feature based on player's tool level.</summary>
    private int GetHitsRequiredForTerrain(TerrainFeature feature)
    {
        int axeLevel = GetAxeLevel();

        if (feature is Tree tree)
        {
            if (tree.stump.Value)
                return GetTreeStumpHits(axeLevel);

            int growthStage = tree.growthStage.Value;

            // Fully grown tree (stage 5)
            if (growthStage >= 5)
            {
                // Hits to chop fully grown tree: 10/8/6/4/2 for basic/copper/steel/gold/iridium
                return axeLevel switch
                {
                    0 => 10, // Basic
                    1 => 8,  // Copper
                    2 => 6,  // Steel
                    3 => 4,  // Gold
                    4 => 2,  // Iridium
                    _ => 10
                };
            }
            // Stage 4 tree
            else if (growthStage == 4)
            {
                return axeLevel switch
                {
                    0 => 5,
                    1 => 4,
                    2 => 3,
                    3 => 2,
                    4 => 1,
                    _ => 5
                };
            }
            // Stage 3 and below (saplings/seeds)
            else
            {
                return axeLevel switch
                {
                    0 => 2,
                    1 => 2,
                    2 => 2,
                    3 => 1,
                    4 => 1,
                    _ => 2
                };
            }
        }

        if (feature is FruitTree fruitTree)
        {
            // Fruit trees also follow similar pattern
            return axeLevel switch
            {
                0 => 10,
                1 => 8,
                2 => 6,
                3 => 4,
                4 => 2,
                _ => 10
            };
        }

        // Grass: 1 hit
        if (feature is Grass) return 1;

        // Bush: varies, typically 3-5 hits
        if (feature is Bush) return 3;

        // Crops: 1 hit to harvest
        if (feature is HoeDirt hoeDirt && hoeDirt.crop != null)
        {
            return 1;
        }

        return 1;
    }

    private static int GetTreeStumpHits(int axeLevel)
    {
        return axeLevel switch
        {
            0 => 5,
            1 => 4,
            2 => 3,
            3 => 2,
            4 => 1,
            _ => 5
        };
    }

    private static string GetTreeState(Tree tree)
    {
        if (tree.stump.Value) return "stump";
        if (tree.growthStage.Value >= 5) return "mature_tree";
        return $"young_tree_stage_{tree.growthStage.Value}";
    }

    /// <summary>Calculate hits required for a resource clump based on player's tool level.</summary>
    private int GetHitsRequiredForResourceClump(string clumpType)
    {
        int axeLevel = GetAxeLevel();
        int pickLevel = GetPickaxeLevel();

        // Large stumps need copper axe minimum
        if (clumpType == "large_stump" || clumpType == "hollow_log")
        {
            // Stump hits: 5/4/3/2/1 for basic/copper/steel/gold/iridium (but need copper+ to break)
            return axeLevel switch
            {
                0 => 999, // Can't break with basic axe
                1 => 5,   // Copper
                2 => 4,   // Steel
                3 => 3,   // Gold
                4 => 2,   // Iridium
                _ => 999
            };
        }

        // Boulders and meteorites need steel pickaxe minimum
        if (clumpType == "boulder" || clumpType == "meteor")
        {
            return pickLevel switch
            {
                0 => 999, // Can't break
                1 => 999, // Can't break
                2 => 8,   // Steel
                3 => 6,   // Gold
                4 => 4,   // Iridium
                _ => 999
            };
        }

        // Mine rocks
        if (clumpType.StartsWith("mine_rock"))
        {
            return pickLevel switch
            {
                0 => 2,
                1 => 1,
                2 => 1,
                3 => 1,
                4 => 1,
                _ => 2
            };
        }

        return 5; // Default
    }

    private List<NearbyTerrainFeature> GetNearbyTerrainFeatures(GameLocation location, int centerX, int centerY)
    {
        var features = new List<NearbyTerrainFeature>();

        try
        {
            // Create snapshot to avoid concurrent modification
            var terrainPairs = location.terrainFeatures.Pairs.ToList();

            foreach (var kvp in terrainPairs)
            {
                var pos = kvp.Key;
                var feature = kvp.Value;

                if (Math.Abs(pos.X - centerX) <= ScanRadius && Math.Abs(pos.Y - centerY) <= ScanRadius)
                {
                    var featureInfo = new NearbyTerrainFeature
                    {
                        X = (int)pos.X,
                        Y = (int)pos.Y,
                        Type = GetTerrainFeatureType(feature),
                        IsPassable = feature.isPassable(),
                        RequiredTool = GetRequiredToolForTerrain(feature),
                        HitsRequired = GetHitsRequiredForTerrain(feature)
                    };

                    // Add specific info based on type
                    if (feature is Tree tree)
                    {
                        int currentHits = GetHitsRequiredForTerrain(tree);
                        int stumpHits = GetTreeStumpHits(GetAxeLevel());
                        featureInfo.GrowthStage = tree.growthStage.Value;
                        featureInfo.IsStump = tree.stump.Value;
                        featureInfo.TreeState = GetTreeState(tree);
                        featureInfo.IsFullyGrown = !tree.stump.Value && tree.growthStage.Value >= 5;
                        featureInfo.HasSeed = tree.hasSeed.Value;
                        featureInfo.CanBeChopped = !tree.stump.Value && tree.growthStage.Value >= 5;
                        featureInfo.HitsRequired = currentHits;
                        featureInfo.EstimatedTotalHitsToRemove = tree.stump.Value
                            ? currentHits
                            : tree.growthStage.Value >= 5 ? currentHits + stumpHits : currentHits;
                        featureInfo.RemovalSequence = tree.stump.Value
                            ? "axe_repeatedly_until_terrain_feature_disappears"
                            : tree.growthStage.Value >= 5
                                ? "axe_repeatedly_until_tree_falls_then_continue_on_stump_until_terrain_feature_disappears"
                                : "axe_repeatedly_until_terrain_feature_disappears";
                    }
                    else if (feature is FruitTree fruitTree)
                    {
                        featureInfo.GrowthStage = fruitTree.growthStage.Value;
                        featureInfo.IsFullyGrown = fruitTree.growthStage.Value >= 4;
                        featureInfo.FruitCount = fruitTree.fruit.Count;
                    }
                    else if (feature is HoeDirt hoeDirt)
                    {
                        featureInfo.IsWatered = hoeDirt.state.Value == 1;
                        featureInfo.HasCrop = hoeDirt.crop != null;
                        if (hoeDirt.crop != null)
                        {
                            string harvestId = hoeDirt.crop.indexOfHarvest.Value;
                            featureInfo.CropName = harvestId;
                            try { featureInfo.CropName = ItemRegistry.Create(harvestId).DisplayName; } catch { }
                            featureInfo.CropPhase = hoeDirt.crop.currentPhase.Value;
                            featureInfo.DaysUntilHarvest = CropDaysRemaining(hoeDirt.crop);
                            featureInfo.IsReadyForHarvest = CropReady(hoeDirt.crop);
                            featureInfo.IsDead = hoeDirt.crop.dead.Value;
                            featureInfo.HarvestMethod = hoeDirt.crop.GetData()?.HarvestMethod.ToString() ?? "UNKNOWN";
                        }
                    }
                    else if (feature is Bush bush)
                    {
                        featureInfo.IsFullyGrown = bush.size.Value >= 3; // Large bush size
                        featureInfo.IsReadyForHarvest = bush.inBloom();
                    }
                    else if (feature is Grass grass)
                    {
                        featureInfo.GrassType = grass.grassType.Value;
                    }

                    features.Add(featureInfo);
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return features;
    }

    private static bool CropReady(Crop? crop) => crop != null && !crop.dead.Value && crop.phaseDays.Count > 0
        && crop.currentPhase.Value >= crop.phaseDays.Count - 1
        && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);

    private static int CropDaysRemaining(Crop crop)
    {
        if (CropReady(crop)) return 0;
        if (crop.phaseDays.Count < 2) return 0;
        int lastGrowthPhase = crop.phaseDays.Count - 2;
        int phase = Math.Clamp(crop.currentPhase.Value, 0, lastGrowthPhase);
        int remaining = Math.Max(0, crop.phaseDays[phase] - Math.Max(0, crop.dayOfCurrentPhase.Value));
        for (int i = phase + 1; i <= lastGrowthPhase; i++) remaining += Math.Max(0, crop.phaseDays[i]);
        return Math.Clamp(remaining, 0, 28);
    }

    private string GetTerrainFeatureType(TerrainFeature feature)
    {
        return feature switch
        {
            Tree => "tree",
            FruitTree => "fruit_tree",
            HoeDirt => "hoe_dirt",
            Grass => "grass",
            Bush => "bush",
            Flooring => "flooring",
            _ => "unknown"
        };
    }

    private List<NearbyNPC> GetNearbyNPCs(GameLocation location, int centerX, int centerY)
    {
        var npcs = new List<NearbyNPC>();

        try
        {
            // Create snapshot to avoid concurrent modification
            var characters = location.characters.ToList();

            foreach (var npc in characters)
            {
                if (npc is Monster) continue; // Handle monsters separately

                int npcX = (int)npc.Tile.X;
                int npcY = (int)npc.Tile.Y;

                if (Math.Abs(npcX - centerX) <= ScanRadius && Math.Abs(npcY - centerY) <= ScanRadius)
                {
                    npcs.Add(new NearbyNPC
                    {
                        X = npcX,
                        Y = npcY,
                        Name = npc.Name,
                        DisplayName = npc.displayName,
                        IsFacingPlayer = IsFacingPosition(npc, centerX, centerY),
                        CanTalk = npc.CanSocialize,
                        FriendshipLevel = Game1.player.getFriendshipLevelForNPC(npc.Name),
                        IsMoving = npc.isMoving()
                    });
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return npcs;
    }

    private List<NearbyMonster> GetNearbyMonsters(GameLocation location, int centerX, int centerY)
    {
        var monsters = new List<NearbyMonster>();

        try
        {
            // Create snapshot to avoid concurrent modification
            var characters = location.characters.ToList();

            foreach (var character in characters)
            {
                if (character is not Monster monster) continue;

                int monsterX = (int)monster.Tile.X;
                int monsterY = (int)monster.Tile.Y;

                if (Math.Abs(monsterX - centerX) <= ScanRadius && Math.Abs(monsterY - centerY) <= ScanRadius)
                {
                    monsters.Add(new NearbyMonster
                    {
                        X = monsterX,
                        Y = monsterY,
                        Name = monster.Name,
                        Health = monster.Health,
                        MaxHealth = monster.MaxHealth,
                        DamageToFarmer = monster.DamageToFarmer,
                        IsGlider = monster.isGlider.Value,
                        Distance = Math.Abs(monsterX - centerX) + Math.Abs(monsterY - centerY)
                    });
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return monsters.OrderBy(m => m.Distance).ToList();
    }

    private List<NearbyResourceClump> GetNearbyResourceClumps(GameLocation location, int centerX, int centerY)
    {
        var clumps = new List<NearbyResourceClump>();

        try
        {
            // Create snapshot to avoid concurrent modification
            var resourceClumps = location.resourceClumps.ToList();

            foreach (var clump in resourceClumps)
            {
                int clumpX = (int)clump.Tile.X;
                int clumpY = (int)clump.Tile.Y;

                if (Math.Abs(clumpX - centerX) <= ScanRadius && Math.Abs(clumpY - centerY) <= ScanRadius)
                {
                    var clumpType = GetResourceClumpType(clump.parentSheetIndex.Value);
                    clumps.Add(new NearbyResourceClump
                    {
                        X = clumpX,
                        Y = clumpY,
                        Width = clump.width.Value,
                        Height = clump.height.Value,
                        Type = clumpType,
                        Health = clump.health.Value,
                        RequiredTool = GetRequiredToolForResourceClump(clumpType),
                        HitsRequired = GetHitsRequiredForResourceClump(clumpType)
                    });
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return clumps;
    }

    private string GetResourceClumpType(int index)
    {
        return index switch
        {
            600 => "large_stump",
            602 => "hollow_log",
            622 => "meteor",
            672 => "boulder",
            752 => "mine_rock_1",
            754 => "mine_rock_2",
            756 => "mine_rock_3",
            758 => "mine_rock_4",
            _ => $"resource_{index}"
        };
    }

    /// <summary>Determine what tool is needed for a resource clump.</summary>
    private string? GetRequiredToolForResourceClump(string clumpType)
    {
        return clumpType switch
        {
            "large_stump" => "Axe",
            "hollow_log" => "Axe",
            "meteor" => "Pickaxe",
            "boulder" => "Pickaxe",
            var s when s.StartsWith("mine_rock") => "Pickaxe",
            _ => "Pickaxe" // Default to pickaxe for unknown resource clumps
        };
    }

    private List<NearbyDebris> GetNearbyDebris(GameLocation location, int centerX, int centerY)
    {
        var debris = new List<NearbyDebris>();

        try
        {
            // Create snapshot to avoid concurrent modification
            var objectPairs = location.Objects.Pairs.ToList();

            // Check for forage items and debris in objects
            foreach (var kvp in objectPairs)
            {
                var pos = kvp.Key;
                var obj = kvp.Value;

                if (Math.Abs(pos.X - centerX) <= ScanRadius && Math.Abs(pos.Y - centerY) <= ScanRadius)
                {
                    if (obj.IsSpawnedObject || obj.isForage())
                    {
                        debris.Add(new NearbyDebris
                        {
                            X = (int)pos.X,
                            Y = (int)pos.Y,
                            Name = obj.Name,
                            Type = obj.isForage() ? "forage" : "spawned",
                            CanBePickedUp = true,
                            QualifiedItemId = obj.QualifiedItemId,
                            Stack = obj.Stack,
                            Source = "map_object",
                            ChunkCount = 1
                        });
                    }
                    else if (IsDebrisObject(obj))
                    {
                        debris.Add(new NearbyDebris
                        {
                            X = (int)pos.X,
                            Y = (int)pos.Y,
                            Name = obj.Name,
                            Type = GetDebrisType(obj),
                            CanBePickedUp = false,
                            QualifiedItemId = obj.QualifiedItemId,
                            Stack = obj.Stack,
                            Source = "map_obstacle",
                            ChunkCount = 1
                        });
                    }
                }
            }

            // Tree, rock and crop drops are Debris entities, not map Objects. The
            // previous serializer omitted this collection entirely, so freshly
            // dropped wood, sap and seeds were invisible to the agent.
            foreach (var loose in location.debris.ToList())
            {
                if (loose.Chunks == null || loose.Chunks.Count == 0)
                    continue;
                var chunk = loose.Chunks.FirstOrDefault();
                if (chunk == null)
                    continue;
                Vector2 position = chunk.position.Value;
                int x = (int)Math.Floor(position.X / Game1.tileSize);
                int y = (int)Math.Floor(position.Y / Game1.tileSize);
                if (Math.Abs(x - centerX) > ScanRadius || Math.Abs(y - centerY) > ScanRadius)
                    continue;
                bool collectible = loose.debrisType.Value is Debris.DebrisType.OBJECT
                    or Debris.DebrisType.RESOURCE or Debris.DebrisType.ARCHAEOLOGY;
                Item? item = loose.item;
                string itemId = item?.QualifiedItemId ?? loose.itemId.Value ?? "";
                int stack = item?.Stack ?? 0;
                if (stack <= 0)
                    stack = Math.Max(1, loose.Chunks.Count);
                debris.Add(new NearbyDebris
                {
                    X = x,
                    Y = y,
                    Name = item?.DisplayName ?? (itemId == "" ? loose.debrisType.Value.ToString() : itemId),
                    Type = "loose_item",
                    CanBePickedUp = collectible,
                    QualifiedItemId = itemId,
                    Stack = stack,
                    Source = "location_debris",
                    ChunkCount = loose.Chunks.Count
                });
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return debris;
    }

    private bool IsDebrisObject(SObject obj)
    {
        // Stone, twigs, weeds
        return obj.Name.Contains("Stone") || obj.Name.Contains("Twig") || obj.Name.Contains("Weed");
    }

    private string GetDebrisType(SObject obj)
    {
        if (obj.Name.Contains("Stone")) return "stone";
        if (obj.Name.Contains("Twig")) return "twig";
        if (obj.Name.Contains("Weed")) return "weed";
        return "debris";
    }

    private List<NearbyBuilding> GetNearbyBuildings(GameLocation location, int centerX, int centerY)
    {
        var buildings = new List<NearbyBuilding>();

        try
        {
            if (location.buildings == null || location.buildings.Count == 0)
                return buildings;

            // Create snapshot to avoid concurrent modification
            var buildingList = location.buildings.ToList();

            foreach (var building in buildingList)
            {
                int buildingX = building.tileX.Value;
                int buildingY = building.tileY.Value;

                // Check if building is within extended radius (buildings can be large)
                if (Math.Abs(buildingX - centerX) <= ScanRadius + 5 && Math.Abs(buildingY - centerY) <= ScanRadius + 5)
                {
                    buildings.Add(new NearbyBuilding
                    {
                        X = buildingX,
                        Y = buildingY,
                        Width = building.tilesWide.Value,
                        Height = building.tilesHigh.Value,
                        Type = building.buildingType.Value,
                        DoorX = buildingX + building.humanDoor.X,
                        DoorY = buildingY + building.humanDoor.Y,
                        HasAnimals = building.GetIndoors() is AnimalHouse
                    });
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return buildings;
    }

    private List<NearbyAnimal> GetNearbyAnimals(GameLocation location, int centerX, int centerY)
    {
        var animals = new List<NearbyAnimal>();

        try
        {
            // Check farm animals - create snapshot to avoid concurrent modification
            List<FarmAnimal>? farmAnimals = null;
            if (location is Farm farm)
                farmAnimals = farm.animals.Values.ToList();
            else if (location is AnimalHouse animalHouse)
                farmAnimals = animalHouse.animals.Values.ToList();

            if (farmAnimals != null)
            {
                foreach (var animal in farmAnimals)
                {
                    int animalX = (int)animal.Tile.X;
                    int animalY = (int)animal.Tile.Y;

                    if (Math.Abs(animalX - centerX) <= ScanRadius && Math.Abs(animalY - centerY) <= ScanRadius)
                    {
                        animals.Add(new NearbyAnimal
                        {
                            X = animalX,
                            Y = animalY,
                            Name = animal.Name,
                            Type = animal.type.Value,
                            Age = animal.age.Value,
                            Happiness = animal.happiness.Value,
                            CanBePet = !animal.wasPet.Value,
                            HasProduce = !string.IsNullOrEmpty(animal.currentProduce.Value)
                        });
                    }
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return animals;
    }

    private List<WarpPoint> GetWarpPoints(GameLocation location)
    {
        var warps = new List<WarpPoint>();

        if (location == null) return warps;

        try
        {
            // Create snapshots to avoid concurrent modification
            var warpList = location.warps.ToList();
            var doorList = location.doors.Pairs.ToList();

            // Get warps from map properties
            foreach (var warp in warpList)
            {
                warps.Add(new WarpPoint
                {
                    X = warp.X,
                    Y = warp.Y,
                    TargetLocation = warp.TargetName,
                    TargetX = warp.TargetX,
                    TargetY = warp.TargetY
                });
            }

            // Add doors
            foreach (var door in doorList)
            {
                warps.Add(new WarpPoint
                {
                    X = (int)door.Key.X,
                    Y = (int)door.Key.Y,
                    TargetLocation = door.Value,
                    IsDoor = true
                });
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return warps;
    }

    private TileInFront GetTileInFront(GameLocation location, int playerX, int playerY, int facingDirection)
    {
        int targetX = playerX;
        int targetY = playerY;

        switch (facingDirection)
        {
            case 0: targetY--; break; // Up
            case 1: targetX++; break; // Right
            case 2: targetY++; break; // Down
            case 3: targetX--; break; // Left
        }

        var tileVector = new Vector2(targetX, targetY);
        var tileLocation = new Location(targetX, targetY);

        var info = new TileInFront
        {
            X = targetX,
            Y = targetY,
            IsPassable = location.isTilePassable(tileLocation, Game1.viewport) && !IsTileOccupied(location, targetX, targetY),
            IsWater = location.isWaterTile(targetX, targetY),
            IsTillable = location.doesTileHaveProperty(targetX, targetY, "Diggable", "Back") != null
        };

        try
        {
            // Check what's on this tile
            if (location.Objects.TryGetValue(tileVector, out var obj))
            {
                info.ObjectName = obj.Name;
                info.ObjectType = GetObjectType(obj);
                info.CanInteract = true;
                info.RequiredTool = GetRequiredToolForObject(obj);
            }

            if (location.terrainFeatures.TryGetValue(tileVector, out var feature))
            {
                info.TerrainType = GetTerrainFeatureType(feature);
                info.CanInteract = true;
                info.RequiredTool ??= GetRequiredToolForTerrain(feature);
            }

            // Check for resource clumps
            foreach (var clump in location.resourceClumps)
            {
                if (clump.occupiesTile(targetX, targetY))
                {
                    var clumpType = GetResourceClumpType(clump.parentSheetIndex.Value);
                    info.ObjectName = clumpType;
                    info.ObjectType = "resource_clump";
                    info.CanInteract = true;
                    info.RequiredTool ??= GetRequiredToolForResourceClump(clumpType);
                    break;
                }
            }

            // Check for NPCs - create snapshot
            var characters = location.characters.ToList();
            foreach (var npc in characters)
            {
                if ((int)npc.Tile.X == targetX && (int)npc.Tile.Y == targetY)
                {
                    info.NPCName = npc.Name;
                    info.CanInteract = true;
                    break;
                }
            }
        }
        catch { /* Ignore concurrent modification errors */ }

        return info;
    }

    private bool IsFacingPosition(NPC npc, int targetX, int targetY)
    {
        int npcX = (int)npc.Tile.X;
        int npcY = (int)npc.Tile.Y;

        return npc.FacingDirection switch
        {
            0 => targetY < npcY, // Up
            1 => targetX > npcX, // Right
            2 => targetY > npcY, // Down
            3 => targetX < npcX, // Left
            _ => false
        };
    }

    private List<InventoryItem> GetInventory(Farmer player)
    {
        var items = new List<InventoryItem>();
        for (int i = 0; i < player.Items.Count && i < 36; i++)
        {
            var item = player.Items[i];
            if (item != null)
            {
                items.Add(new InventoryItem
                {
                    Slot = i,
                    Name = item.Name,
                    DisplayName = item.DisplayName,
                    Stack = item.Stack,
                    Category = item.getCategoryName(),
                    IsTool = item is Tool,
                    IsWeapon = item is StardewValley.Tools.MeleeWeapon
                });
            }
        }
        return items;
    }

    private string GetWeather()
    {
        if (Game1.isRaining)
            return Game1.isLightning ? "stormy" : "rainy";
        if (Game1.isSnowing)
            return "snowy";
        if (Game1.isDebrisWeather)
            return "windy";
        return "sunny";
    }

    private string GetDayOfWeek(int dayOfMonth)
    {
        return ((dayOfMonth - 1) % 7) switch
        {
            0 => "Monday",
            1 => "Tuesday",
            2 => "Wednesday",
            3 => "Thursday",
            4 => "Friday",
            5 => "Saturday",
            6 => "Sunday",
            _ => "Unknown"
        };
    }

    private string GetDirectionName(int direction)
    {
        return direction switch
        {
            0 => "up",
            1 => "right",
            2 => "down",
            3 => "left",
            _ => "unknown"
        };
    }

    private string FormatTime(int time)
    {
        int hours = time / 100;
        int minutes = time % 100;
        string period = hours >= 12 ? "PM" : "AM";
        if (hours > 12) hours -= 12;
        if (hours == 0) hours = 12;
        return $"{hours}:{minutes:D2} {period}";
    }

    private int GetMinutesUntilMorning()
    {
        int currentTime = Game1.timeOfDay;
        if (currentTime >= 600 && currentTime < 2600)
        {
            // Time until 2:00 AM (2600)
            int hours = (2600 - currentTime) / 100;
            int minutes = (2600 - currentTime) % 100;
            return hours * 60 + minutes;
        }
        return 0;
    }

    private string GetLocationType(GameLocation location)
    {
        if (location == null) return "unknown";
        if (location is Farm) return "farm";
        if (location is FarmHouse) return "farmhouse";
        if (location is MineShaft) return "mine";
        if (location is Town) return "town";
        if (location is Beach) return "beach";
        if (location is Forest) return "forest";
        if (location is Mountain) return "mountain";
        if (location is AnimalHouse) return "animal_house";
        if (location.IsGreenhouse) return "greenhouse";
        return "other";
    }
}

#region State Classes

public class GameState
{
    public PlayerState Player { get; set; } = new();
    public TimeState Time { get; set; } = new();
    public WorldState World { get; set; } = new();
    public SurroundingsState Surroundings { get; set; } = new();
    public MapInfo Map { get; set; } = new();
    public List<QuestInfo> Quests { get; set; } = new();
    public List<RelationshipInfo> Relationships { get; set; } = new();
    public SkillsInfo Skills { get; set; } = new();
}

public class QuestInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Objective { get; set; } = "";
    public bool IsComplete { get; set; }
    public int DaysLeft { get; set; }
    public int Reward { get; set; }
}

public class RelationshipInfo
{
    public string NpcName { get; set; } = "";
    public int FriendshipPoints { get; set; }
    public int Hearts { get; set; }
    public int GiftsToday { get; set; }
    public int GiftsThisWeek { get; set; }
    public bool TalkedToToday { get; set; }
    public string Status { get; set; } = "";
}

public class SkillsInfo
{
    public int Farming { get; set; }
    public int Mining { get; set; }
    public int Foraging { get; set; }
    public int Fishing { get; set; }
    public int Combat { get; set; }
    public int FarmingXp { get; set; }
    public int MiningXp { get; set; }
    public int ForagingXp { get; set; }
    public int FishingXp { get; set; }
    public int CombatXp { get; set; }
}

public class PlayerState
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Location { get; set; } = "";
    public float Energy { get; set; }
    public int MaxEnergy { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Money { get; set; }
    public string CurrentTool { get; set; } = "";
    public int CurrentToolIndex { get; set; }
    public int FacingDirection { get; set; }
    public string FacingDirectionName { get; set; } = "";
    public bool IsMoving { get; set; }
    public bool CanMove { get; set; }
    public bool ShopOpen { get; set; }
    // Pathfinding state - from move_to command
    public bool IsPathfinding { get; set; }
    public int? PathfindingTargetX { get; set; }
    public int? PathfindingTargetY { get; set; }
    public int? PathProgress { get; set; }
    public int? PathLength { get; set; }
    public List<InventoryItem> Inventory { get; set; } = new();
}

public class TimeState
{
    public int TimeOfDay { get; set; }
    public string TimeString { get; set; } = "";
    public int Day { get; set; }
    public string Season { get; set; } = "";
    public int Year { get; set; }
    public string DayOfWeek { get; set; } = "";
    public bool IsNight { get; set; }
    public int MinutesUntilMorning { get; set; }
}

public class WorldState
{
    public string Weather { get; set; } = "";
    public bool IsOutdoors { get; set; }
    public bool IsFarm { get; set; }
    public bool IsGreenhouse { get; set; }
    public bool IsBuildableLocation { get; set; }
    public string LocationType { get; set; } = "";
}

public class SurroundingsState
{
    public string FurnitureInfo { get; set; } = "";
    public string AsciiMap { get; set; } = "";
    public List<TileInfo> NearbyTiles { get; set; } = new();
    public List<NearbyObject> NearbyObjects { get; set; } = new();
    public List<NearbyTerrainFeature> NearbyTerrainFeatures { get; set; } = new();
    public List<NearbyNPC> NearbyNPCs { get; set; } = new();
    public List<NearbyMonster> NearbyMonsters { get; set; } = new();
    public List<NearbyResourceClump> NearbyResourceClumps { get; set; } = new();
    public List<NearbyDebris> NearbyDebris { get; set; } = new();
    public List<NearbyBuilding> NearbyBuildings { get; set; } = new();
    public List<NearbyAnimal> NearbyAnimals { get; set; } = new();
    public List<WarpPoint> WarpPoints { get; set; } = new();
    public TileInFront TileInFront { get; set; } = new();
}

public class MapInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMineLevel { get; set; }
    public int MineLevel { get; set; }
    public string UniqueId { get; set; } = "";
}

public class TileInfo
{
    public int X { get; set; }
    public int Y { get; set; }
    public bool IsPassable { get; set; }
    public bool IsWater { get; set; }
    public bool IsTillable { get; set; }
    public bool HasObject { get; set; }
    public bool HasTerrainFeature { get; set; }
    public string TileType { get; set; } = "";
}

public class NearbyObject
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsPassable { get; set; }
    public bool CanBePickedUp { get; set; }
    public bool IsReadyForHarvest { get; set; }
    public int MinutesUntilReady { get; set; }
    public string? HeldItemName { get; set; }
    public string? RequiredTool { get; set; }
    public int HitsRequired { get; set; } = 1; // Number of tool hits to destroy
}

public class NearbyTerrainFeature
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Type { get; set; } = "";
    public bool IsPassable { get; set; }
    public int GrowthStage { get; set; }
    public bool IsStump { get; set; }
    public string TreeState { get; set; } = "";
    public bool IsFullyGrown { get; set; }
    public bool HasSeed { get; set; }
    public bool CanBeChopped { get; set; }
    public int FruitCount { get; set; }
    public bool IsWatered { get; set; }
    public bool HasCrop { get; set; }
    public string? CropName { get; set; }
    public int CropPhase { get; set; }
    public int DaysUntilHarvest { get; set; }
    public bool IsReadyForHarvest { get; set; }
    public bool IsDead { get; set; }
    public string HarvestMethod { get; set; } = "";
    public int GrassType { get; set; }
    public string? RequiredTool { get; set; }
    public int HitsRequired { get; set; } = 1; // Number of tool hits to destroy
    public int EstimatedTotalHitsToRemove { get; set; }
    public string RemovalSequence { get; set; } = "";
}

public class NearbyNPC
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsFacingPlayer { get; set; }
    public bool CanTalk { get; set; }
    public int FriendshipLevel { get; set; }
    public bool IsMoving { get; set; }
}

public class NearbyMonster
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; } = "";
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int DamageToFarmer { get; set; }
    public bool IsGlider { get; set; }
    public int Distance { get; set; }
}

public class NearbyResourceClump
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Type { get; set; } = "";
    public float Health { get; set; }
    public string? RequiredTool { get; set; }
    public int HitsRequired { get; set; } = 5; // Number of tool hits to destroy
}

public class NearbyDebris
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool CanBePickedUp { get; set; }
    public string QualifiedItemId { get; set; } = "";
    public int Stack { get; set; }
    public string Source { get; set; } = "";
    public int ChunkCount { get; set; }
}

public class NearbyBuilding
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Type { get; set; } = "";
    public int DoorX { get; set; }
    public int DoorY { get; set; }
    public bool HasAnimals { get; set; }
}

public class NearbyAnimal
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Age { get; set; }
    public int Happiness { get; set; }
    public bool CanBePet { get; set; }
    public bool HasProduce { get; set; }
}

public class WarpPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public string TargetLocation { get; set; } = "";
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public bool IsDoor { get; set; }
}

public class TileInFront
{
    public int X { get; set; }
    public int Y { get; set; }
    public bool IsPassable { get; set; }
    public bool IsWater { get; set; }
    public bool IsTillable { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public string? TerrainType { get; set; }
    public string? NPCName { get; set; }
    public bool CanInteract { get; set; }
    public string? RequiredTool { get; set; }
}

public class InventoryItem
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Stack { get; set; }
    public string Category { get; set; } = "";
    public bool IsTool { get; set; }
    public bool IsWeapon { get; set; }
}

#endregion
