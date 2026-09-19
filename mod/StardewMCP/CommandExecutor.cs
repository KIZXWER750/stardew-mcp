using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Buildings;
using StardewValley.Quests;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCP;

/// <summary>Executes commands received from the WebSocket server.</summary>
public partial class CommandExecutor
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ConcurrentQueue<GameCommand> _commandQueue = new();
    private readonly Pathfinder _pathfinder = new();

    // Movement state - now uses a calculated path
    private List<Vector2>? _currentPath;
    private int _pathIndex;
    private Vector2? _finalTarget;
    private Action<CommandResponse>? _moveCallback;
    private string? _moveCommandId;
    private int _stuckCounter;
    private int _recalculationAttempts;
    private const int MaxRecalculationAttempts = 5; // Max path recalculation attempts before giving up

    // Tool use state - for repeated tool usage
    private int _toolUseCount;
    private int _toolUseRemaining;
    private int _toolUseCooldown;
    private const int ToolUseCooldownTicks = 30; // ~0.5 seconds between swings at 60fps
    private Action<CommandResponse>? _toolCallback;
    private string? _toolCommandId;
    private string? _toolName;

    // Hold tool state - for charged tool usage (watering can, hoe)
    private int _holdToolTicks;
    private int _holdToolRemaining;
    private Action<CommandResponse>? _holdToolCallback;
    private string? _holdToolCommandId;
    private string? _holdToolName;
    private bool _holdToolReleased;

    // Cheat mode state
    private bool _cheatModeEnabled = false;
    private bool _infiniteEnergyEnabled = false;
    private bool _timeFreezeEnabled = false;
    private int _frozenTime = -1;

    public CommandExecutor(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    // Public properties for movement state (used by GameStateSerializer)
    public bool IsMoving => _currentPath != null && _pathIndex < _currentPath.Count;
    public Vector2? MovementTarget => _finalTarget;
    public int? PathLength => _currentPath?.Count;
    public int? PathProgress => _currentPath != null ? _pathIndex : null;

    /// <summary>Extract an integer from a parameter that may be a JsonElement.</summary>
    private int GetIntParam(object obj, int defaultValue = 0)
    {
        if (obj is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : defaultValue;
        }
        return Convert.ToInt32(obj);
    }

    /// <summary>Extract a string from a parameter that may be a JsonElement.</summary>
    private string GetStringParam(object? obj, string defaultValue = "")
    {
        if (obj is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() ?? defaultValue : defaultValue;
        }
        return obj?.ToString() ?? defaultValue;
    }

    /// <summary>Queue a command for execution on the game thread.</summary>
    public CommandResponse QueueCommand(GameCommand command)
    {
        _monitor.Log($"Queuing command: {command.Action}", LogLevel.Debug);
        _commandQueue.Enqueue(command);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Command queued"
        };
    }

    /// <summary>Process pending commands and continuous movement (called from game loop every tick).</summary>
    public void ProcessPendingCommands()
    {
        // Enforce cheat mode effects
        EnforceCheatModeEffects();

        // Process new commands (only if not busy with tool use)
        if (_toolUseRemaining == 0 && _commandQueue.TryDequeue(out var command))
        {
            ExecuteCommand(command);
        }

        // Continue movement if we have a target
        ProcessMovement();
        ProcessExitHouse();

        // Continue tool use if active
        ProcessToolUse();

        // Continue held tool if active
        ProcessHoldTool();
        UpdateFarmWork();
        UpdateShopExit();
    }

    /// <summary>Enforce active cheat mode effects like time freeze and infinite energy.</summary>
    private void EnforceCheatModeEffects()
    {
        if (!_cheatModeEnabled) return;

        // Enforce time freeze
        if (_timeFreezeEnabled && _frozenTime > 0)
        {
            Game1.timeOfDay = _frozenTime;
        }

        // Enforce infinite energy
        if (_infiniteEnergyEnabled)
        {
            Game1.player.Stamina = Game1.player.MaxStamina;
        }
    }

    /// <summary>Set cursor position to the tile in front of the player based on facing direction.</summary>
    private void SetCursorToFacingTile()
    {
        var player = Game1.player;
        int tileX = (int)player.Tile.X;
        int tileY = (int)player.Tile.Y;

        // Calculate tile in front based on facing direction
        // 0 = up, 1 = right, 2 = down, 3 = left
        switch (player.FacingDirection)
        {
            case 0: tileY--; break; // up
            case 1: tileX++; break; // right
            case 2: tileY++; break; // down
            case 3: tileX--; break; // left
        }

        // Convert tile to world pixel position (center of tile)
        // Tiles are 64x64 pixels, add 32 to get center
        int worldX = (tileX * 64) + 32;
        int worldY = (tileY * 64) + 32;

        // Set the cursor tile directly - this is more reliable than screen coordinates
        Game1.currentCursorTile = new Vector2(tileX, tileY);

        // Also set last cursor motion to non-mouse so game uses facing direction
        Game1.lastCursorMotionWasMouse = false;

        // Set screen position too for visual feedback
        int screenX = worldX - Game1.viewport.X;
        int screenY = worldY - Game1.viewport.Y;
        Game1.setMousePosition(screenX, screenY);
    }

    /// <summary>Process repeated tool usage.</summary>
    private void ProcessToolUse()
    {
        if (_toolUseRemaining <= 0)
            return;

        // Decrement cooldown
        if (_toolUseCooldown > 0)
        {
            _toolUseCooldown--;
            // Keep enforcing non-mouse targeting during cooldown too
            Game1.lastCursorMotionWasMouse = false;
            return;
        }

        // Check if player can act (not in animation)
        var player = Game1.player;
        if (player.UsingTool || !player.CanMove)
        {
            // Keep enforcing non-mouse targeting while waiting
            Game1.lastCursorMotionWasMouse = false;
            return;
        }

        // Set cursor to tile in front so tool swings in correct direction
        SetCursorToFacingTile();

        // Force non-mouse mode right before pressing button
        Game1.lastCursorMotionWasMouse = false;

        // Use the tool
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);
        _toolUseRemaining--;
        _toolUseCooldown = ToolUseCooldownTicks;

        _monitor.Log($"Tool swing {_toolUseCount - _toolUseRemaining}/{_toolUseCount}", LogLevel.Debug);

        // Check if done
        if (_toolUseRemaining <= 0)
        {
            _monitor.Log($"Completed {_toolUseCount} tool uses", LogLevel.Debug);

            // player variable already in scope from line 143
            _toolCallback?.Invoke(new CommandResponse
            {
                Id = _toolCommandId ?? "",
                Success = true,
                Message = $"Used {_toolName} {_toolUseCount} times. VERIFY tile-in-front to confirm target was affected!",
                Data = new Dictionary<string, object>
                {
                    ["swings"] = _toolUseCount,
                    ["tool"] = _toolName ?? "Unknown",
                    ["energy"] = player.Stamina,
                    ["note"] = "Read tile-in-front to verify the target (tree/rock/etc) was destroyed or changed"
                }
            });

            ClearToolState();
        }
    }

    /// <summary>Process held tool charging (watering can, hoe).</summary>
    private void ProcessHoldTool()
    {
        if (_holdToolRemaining <= 0)
            return;

        var player = Game1.player;
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        // Keep pressing the button every tick to simulate holding
        if (!_holdToolReleased)
        {
            // Set cursor to tile in front so tool aims in correct direction
            SetCursorToFacingTile();

            // Press every tick to simulate holding
            _helper.Input.Press(useButton);
            _holdToolRemaining--;

            // Check if we should stop (release)
            if (_holdToolRemaining <= 0)
            {
                _holdToolReleased = true;
                // No explicit release needed - just stop pressing

                _monitor.Log($"Released {_holdToolName} after {_holdToolTicks} ticks", LogLevel.Debug);

                _holdToolCallback?.Invoke(new CommandResponse
                {
                    Id = _holdToolCommandId ?? "",
                    Success = true,
                    Message = $"Used {_holdToolName} with {_holdToolTicks} tick charge",
                    Data = new Dictionary<string, object>
                    {
                        ["ticks"] = _holdToolTicks,
                        ["tool"] = _holdToolName ?? "Unknown"
                    }
                });

                ClearHoldToolState();
            }
        }
    }

    /// <summary>Clear hold tool state.</summary>
    private void ClearHoldToolState()
    {
        _holdToolTicks = 0;
        _holdToolRemaining = 0;
        _holdToolCallback = null;
        _holdToolCommandId = null;
        _holdToolName = null;
        _holdToolReleased = false;
    }

    /// <summary>Clear tool use state.</summary>
    private void ClearToolState()
    {
        _toolUseCount = 0;
        _toolUseRemaining = 0;
        _toolUseCooldown = 0;
        _toolCallback = null;
        _toolCommandId = null;
        _toolName = null;
    }

    /// <summary>Process continuous movement along calculated path.</summary>
    private void ProcessMovement()
    {
        if (_currentPath == null || _pathIndex >= _currentPath.Count)
            return;

        var player = Game1.player;
        var currentPos = new Vector2((int)player.Tile.X, (int)player.Tile.Y);

        // Get current target tile in path
        var targetTile = _currentPath[_pathIndex];

        // Check if arrived at current waypoint
        if (currentPos == targetTile)
        {
            _pathIndex++;
            _stuckCounter = 0;

            // Check if we've completed the entire path
            if (_pathIndex >= _currentPath.Count)
            {
                _monitor.Log($"Arrived at final destination ({targetTile.X}, {targetTile.Y})", LogLevel.Debug);

                // Include full current state for verification
                var actualPos = Game1.player.Tile;
                _moveCallback?.Invoke(new CommandResponse
                {
                    Id = _moveCommandId ?? "",
                    Success = true,
                    Message = $"Arrived at ({(int)actualPos.X}, {(int)actualPos.Y}) in {Game1.currentLocation?.Name ?? "Unknown"}",
                    Data = new Dictionary<string, object>
                    {
                        ["arrived"] = true,
                        ["x"] = (int)actualPos.X,
                        ["y"] = (int)actualPos.Y,
                        ["location"] = Game1.currentLocation?.Name ?? "Unknown"
                    }
                });

                ClearMovementState();
                return;
            }

            // Move to next waypoint
            targetTile = _currentPath[_pathIndex];
        }

        // Check if stuck (same position for too long)
        _stuckCounter++;
        if (_stuckCounter > 120) // ~2 seconds at 60fps
        {
            _recalculationAttempts++;
            if (_recalculationAttempts >= MaxRecalculationAttempts)
            {
                var actualPos = Game1.player.Tile;
                _monitor.Log($"Movement failed after {MaxRecalculationAttempts} recalculation attempts", LogLevel.Warn);
                _moveCallback?.Invoke(new CommandResponse
                {
                    Id = _moveCommandId ?? "",
                    Success = false,
                    Message = $"Stuck at ({(int)actualPos.X}, {(int)actualPos.Y}) - could not reach destination after {MaxRecalculationAttempts} attempts",
                    Data = new Dictionary<string, object>
                    {
                        ["arrived"] = false,
                        ["x"] = (int)actualPos.X,
                        ["y"] = (int)actualPos.Y,
                        ["attempts"] = _recalculationAttempts
                    }
                });
                ClearMovementState();
                return;
            }

            _monitor.Log($"Movement stuck, recalculating path (attempt {_recalculationAttempts}/{MaxRecalculationAttempts})...", LogLevel.Debug);
            RecalculatePath();
            _stuckCounter = 0;
            return;
        }

        // Calculate direction to next waypoint
        int dx = (int)(targetTile.X - currentPos.X);
        int dy = (int)(targetTile.Y - currentPos.Y);

        SButton? moveButton = null;
        if (dx > 0) moveButton = GetMoveButton("right");
        else if (dx < 0) moveButton = GetMoveButton("left");
        else if (dy > 0) moveButton = GetMoveButton("down");
        else if (dy < 0) moveButton = GetMoveButton("up");

        if (moveButton.HasValue)
        {
            _helper.Input.Press(moveButton.Value);
        }
    }

    /// <summary>Recalculate path when stuck or blocked.</summary>
    private void RecalculatePath()
    {
        if (_finalTarget == null)
        {
            ClearMovementState();
            return;
        }

        var player = Game1.player;
        var currentPos = new Vector2((int)player.Tile.X, (int)player.Tile.Y);

        var newPath = _pathfinder.FindPath(Game1.currentLocation, currentPos, _finalTarget.Value);

        if (newPath == null || newPath.Count == 0)
        {
            _monitor.Log("Cannot find path to destination", LogLevel.Warn);
            _moveCallback?.Invoke(new CommandResponse
            {
                Id = _moveCommandId ?? "",
                Success = false,
                Message = "Path blocked - cannot reach destination",
                Data = new Dictionary<string, object>
                {
                    ["arrived"] = false,
                    ["x"] = (int)currentPos.X,
                    ["y"] = (int)currentPos.Y
                }
            });
            ClearMovementState();
            return;
        }

        _currentPath = newPath;
        _pathIndex = 0;
        _monitor.Log($"Path recalculated: {newPath.Count} tiles", LogLevel.Debug);
    }

    /// <summary>Clear all movement state.</summary>
    private void ClearMovementState()
    {
        _currentPath = null;
        _pathIndex = 0;
        _finalTarget = null;
        _moveCallback = null;
        _moveCommandId = null;
        _stuckCounter = 0;
        _recalculationAttempts = 0;
    }

    private void ExecuteCommand(GameCommand command)
    {
        if (!AllowUiCommand(command)) {
            command.OnComplete?.Invoke(new CommandResponse {Id=command.Id,Success=false,Message="Inactive UI run or cheat command rejected"});
            return;
        }
        _monitor.Log($"Executing command: {command.Action}", LogLevel.Debug);

        try
        {
            if (_activeShopExit!=null) throw new InvalidOperationException("Exit traversal owns controls; wait or cancel first");
            if (FarmActive && command.Action != "farm_status" && command.Action != "farm_cancel" && command.Action != "farm_inspect" && command.Action != "farm_find_candidates")
                throw new InvalidOperationException("Farm task owns the controls; wait or cancel it first");
            var result = command.Action.ToLower() switch
            {
                "shop_status" => ShopStatus(command),
                "crop_sale_inspect" => InspectSellableCrops(command),
                "crop_sell" => SellCropStack(command),
                "storage_inspect" => InspectStorage(command),
                "storage_inspect_closed" => InspectClosedStorage(command),
                "storage_open" => OpenStorage(command),
                "storage_take" => TakeStorageItem(command),
                "storage_put" => StoreInventoryItem(command),
                "storage_stack_existing" => StackInventoryToStorage(command),
                "storage_organize" => OrganizeStorage(command),
                "storage_close" => CloseStorage(command),
                "memory_search" => SearchMemory(command),
                "memory_chest_get" => GetChestMemoryCommand(command),
                "memory_chest_purpose" => SetChestPurposeCommand(command),
                "memory_note_upsert" => RememberNoteCommand(command),
                "memory_task_list" => ListPersistentTasksCommand(command),
                "memory_task_upsert" => UpsertPersistentTaskCommand(command),
                "memory_task_complete" => CompletePersistentTaskCommand(command),
                "memory_context" => GetRelevantMemoryContextCommand(command),
                "goal_create" => CreateLongTermGoalCommand(command),
                "goal_list" => ListLongTermGoalsCommand(command),
                "goal_inspect" => InspectLongTermGoalCommand(command),
                "goal_status" => TransitionLongTermGoalCommand(command),
                "goal_verify" => VerifyLongTermGoalCommand(command),
                "goal_question" => RequestGoalQuestionCommand(command),
                "knowledge_lookup" => LookupGameKnowledge(command),
                "knowledge_route" => FindWorldRoute(command),
                "shop_route" => FindShopRoute(command),
                "shop_enter_pierre" => EnterPierreShop(command),
                "shop_open_pierre" => OpenPierreShopCounter(command),
                "shop_inspect" => InspectShop(command),
                "shop_buy" => BuyShopItem(command),
                "shop_close" => CloseShop(command),
                "shop_exit" => UseShopExit(command),
                "life_status" => LifeStatus(command),
                "life_food_options" => LifeFoodOptions(command),
                "life_recovery_options" => LifeRecoveryOptions(command),
                "life_eat_step" => LifeEatStep(command),
                "life_enter_farmhouse" => LifeEnterFarmhouse(command),
                "farm_analyze" => FarmAnalyze(command),
                "farm_water_sources" => FarmWaterSources(command),
                "farm_inspect" => FarmInspect(command),
                "farm_find_candidates" => FarmFindCandidates(command),
                "farm_start" => FarmStart(command),
                "farm_status" => FarmStatus(command, false),
                "farm_cancel" => FarmStatus(command, true),
                // Movement & Basic Actions
                "move_to" => ExecuteMoveTo(command),
                "stop" => ExecuteStop(command), "exit_house" => ExecuteExitHouse(command), "sleep_step" => ExecuteSleepStep(command),
                "interact" => ExecuteInteract(command),
                "face_direction" => ExecuteFaceDirection(command),

                // Tool Usage
                "use_tool" => ExecuteUseTool(command),
                "use_tool_repeat" => ExecuteUseToolRepeat(command),
                "hold_tool" => ExecuteHoldTool(command),
                "switch_tool" => ExecuteSwitchTool(command),
                "select_item" => ExecuteSelectItem(command),

                // Inventory Management
                "place_item" => ExecutePlaceItem(command),
                "eat_item" => ExecuteEatItem(command),
                "trash_item" => ExecuteTrashItem(command),
                "ship_item" => ExecuteShipItem(command),

                // Fishing
                "cast_fishing_rod" => ExecuteCastFishingRod(command),
                "reel_fish" => ExecuteReelFish(command),

                // Shopping
                "open_shop_menu" => ExecuteOpenShopMenu(command),
                "buy_item" => ExecuteBuyItem(command),
                "sell_item" => new CommandResponse {Id=command.Id,Success=false,Message="Legacy sale disabled; use crop_sale_inspect and crop_sell"},

                // Social
                "give_gift" => ExecuteGiveGift(command),
                "check_mail" => ExecuteCheckMail(command),

                // Crafting
                "craft_item" => ExecuteCraftItem(command),

                // World Navigation
                "warp_to_location" => ExecuteWarpToLocation(command),
                "enter_door" => ExecuteEnterDoor(command),

                // Combat
                "attack" => ExecuteAttack(command),
                "equip_weapon" => ExecuteEquipWeapon(command),

                // Animal Care
                "pet_animal" => ExecutePetAnimal(command),
                "milk_animal" => ExecuteMilkAnimal(command),
                "shear_animal" => ExecuteShearAnimal(command),
                "collect_product" => ExecuteCollectProduct(command),

                // Mining
                "use_bomb" => ExecuteUseBomb(command),

                // State Query
                "get_state" => ExecuteGetState(command),

                // Cheat Mode Commands
                "cheat_mode_enable" => ExecuteCheatModeEnable(command),
                "cheat_mode_disable" => ExecuteCheatModeDisable(command),
                "cheat_warp" => ExecuteCheatWarp(command),
                "cheat_set_money" => ExecuteCheatSetMoney(command),
                "cheat_add_item" => ExecuteCheatAddItem(command),
                "cheat_set_energy" => ExecuteCheatSetEnergy(command),
                "cheat_set_health" => ExecuteCheatSetHealth(command),
                "cheat_add_experience" => ExecuteCheatAddExperience(command),
                "cheat_harvest_all" => ExecuteCheatHarvestAll(command),
                "cheat_water_all" => ExecuteCheatWaterAll(command),
                "cheat_grow_crops" => ExecuteCheatGrowCrops(command),
                "cheat_clear_debris" => ExecuteCheatClearDebris(command),
                "cheat_mine_warp" => ExecuteCheatMineWarp(command),
                "cheat_spawn_ores" => ExecuteCheatSpawnOres(command),
                "cheat_set_friendship" => ExecuteCheatSetFriendship(command),
                "cheat_max_all_friendships" => ExecuteCheatMaxAllFriendships(command),
                "cheat_collect_all_forage" => ExecuteCheatCollectAllForage(command),
                "cheat_instant_mine" => ExecuteCheatInstantMine(command),
                "cheat_time_set" => ExecuteCheatTimeSet(command),
                "cheat_time_freeze" => ExecuteCheatTimeFreeze(command),
                "cheat_infinite_energy" => ExecuteCheatInfiniteEnergy(command),
                "cheat_unlock_recipes" => ExecuteCheatUnlockRecipes(command),
                "cheat_pet_all_animals" => ExecuteCheatPetAllAnimals(command),
                "cheat_complete_quest" => ExecuteCheatCompleteQuest(command),
                "cheat_give_gift" => ExecuteCheatGiveGift(command),
                "cheat_hoe_all" => ExecuteCheatHoeAll(command),
                "cheat_cut_trees" => ExecuteCheatCutTrees(command),
                "cheat_mine_rocks" => ExecuteCheatMineRocks(command),
                "cheat_dig_artifacts" => ExecuteCheatDigArtifacts(command),
                "cheat_plant_seeds" => ExecuteCheatPlantSeeds(command),
                "cheat_fertilize_all" => ExecuteCheatFertilizeAll(command),
                "cheat_set_season" => ExecuteCheatSetSeason(command),
                
                // Inventory & upgrade cheats
                "cheat_upgrade_backpack" => ExecuteCheatUpgradeBackpack(command),
                "cheat_upgrade_tool" => ExecuteCheatUpgradeTool(command),
                "cheat_upgrade_all_tools" => ExecuteCheatUpgradeAllTools(command),
                "cheat_unlock_all" => ExecuteCheatUnlockAll(command),
                
                // Targeted/selective cheats (for precise control like drawing shapes)
                "cheat_hoe_tiles" => ExecuteCheatHoeTiles(command),
                "cheat_clear_tiles" => ExecuteCheatClearTiles(command),
                "cheat_till_pattern" => ExecuteCheatTillPattern(command),
                "cheat_hoe_custom_pattern" => ExecuteCheatHoeCustomPattern(command),

                _ => new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Unknown action: {command.Action}"
                }
            };

            // For async actions (move_to, use_tool_repeat, hold_tool) - don't invoke callback immediately
            // These actions will invoke the callback when they complete or fail
            var asyncActions = new[] { "shop_exit", "move_to", "use_tool_repeat", "hold_tool" };
            if (!asyncActions.Contains(command.Action.ToLower()))
            {
                command.OnComplete?.Invoke(result);
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Command failed: {ex.Message}", LogLevel.Error);
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = ex.Message
            });
        }
    }

    private CommandResponse ExecuteMoveTo(GameCommand command)
    {
        if (!command.Params.TryGetValue("x", out var xObj) ||
            !command.Params.TryGetValue("y", out var yObj))
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing x or y parameter"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = "Missing x or y parameter" };
        }

        int targetX = GetIntParam(xObj);
        int targetY = GetIntParam(yObj);

        var player = Game1.player;
        var currentPos = new Vector2((int)player.Tile.X, (int)player.Tile.Y);
        var targetPos = new Vector2(targetX, targetY);

        // Check if already at destination
        if (currentPos == targetPos)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = true,
                Message = "Already at destination",
                Data = new Dictionary<string, object>
                {
                    ["arrived"] = true,
                    ["x"] = targetX,
                    ["y"] = targetY
                }
            });
            return new CommandResponse { Id = command.Id, Success = true, Message = "Already at destination" };
        }

        // Calculate path using A*
        var path = _pathfinder.FindPath(Game1.currentLocation, currentPos, targetPos);

        if (path == null)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"No path found to ({targetX}, {targetY})"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = $"No path found to ({targetX}, {targetY})" };
        }

        // Set up movement along the calculated path
        _currentPath = path;
        _pathIndex = 0;
        _finalTarget = targetPos;
        _moveCallback = command.OnComplete; // Store callback for completion/failure notification
        _moveCommandId = command.Id;
        _stuckCounter = 0;
        _recalculationAttempts = 0;

        _monitor.Log($"Path found: {path.Count} tiles from ({currentPos.X}, {currentPos.Y}) to ({targetX}, {targetY})", LogLevel.Info);

        // Return immediately - movement continues in background
        // AI should poll player/status to check isMoving and position
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Movement started to ({targetX}, {targetY}) via {path.Count} tiles. Poll player/status to check progress.",
            Data = new Dictionary<string, object>
            {
                ["started"] = true,
                ["targetX"] = targetX,
                ["targetY"] = targetY,
                ["pathLength"] = path.Count,
                ["note"] = "Movement is non-blocking. Read player/status to check isMoving and current position."
            }
        };
    }

    private CommandResponse ExecuteStop(GameCommand command)
    {
        ClearMovementState();

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Movement stopped"
        };
    }

    private SButton? GetMoveButton(string direction)
    {
        var options = Game1.options;
        return direction.ToLower() switch
        {
            "up" => options.moveUpButton.Length > 0 ? options.moveUpButton[0].ToSButton() : SButton.W,
            "down" => options.moveDownButton.Length > 0 ? options.moveDownButton[0].ToSButton() : SButton.S,
            "left" => options.moveLeftButton.Length > 0 ? options.moveLeftButton[0].ToSButton() : SButton.A,
            "right" => options.moveRightButton.Length > 0 ? options.moveRightButton[0].ToSButton() : SButton.D,
            _ => null
        };
    }

    private CommandResponse ExecuteInteract(GameCommand command)
    {
        // Simulate action button press
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        var player = Game1.player;
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Interaction triggered at ({(int)player.Tile.X}, {(int)player.Tile.Y}) in {Game1.currentLocation?.Name ?? "Unknown"}. VERIFY the expected result occurred!",
            Data = new Dictionary<string, object>
            {
                ["x"] = (int)player.Tile.X,
                ["y"] = (int)player.Tile.Y,
                ["location"] = Game1.currentLocation?.Name ?? "Unknown",
                ["note"] = "Read game state to verify interaction had the expected effect (e.g., world/time for sleeping)"
            }
        };
    }

    private CommandResponse ExecuteUseTool(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No tool equipped"
            };
        }

        // Set cursor to tile in front so tool swings in correct direction
        SetCursorToFacingTile();

        // Force non-mouse mode so game uses facing direction, not real cursor
        Game1.lastCursorMotionWasMouse = false;

        // Simulate use tool button press
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Used tool: {player.CurrentTool.Name}"
        };
    }

    private CommandResponse ExecuteUseToolRepeat(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool == null)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No tool equipped"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = "No tool equipped" };
        }

        // Get count parameter (default to 1)
        int count = 1;
        if (command.Params.TryGetValue("count", out var countObj))
        {
            count = GetIntParam(countObj, 1);
        }

        // Clamp count to reasonable range
        count = Math.Clamp(count, 1, 100);

        // Set up repeated tool use state
        _toolUseCount = count;
        _toolUseRemaining = count;
        _toolUseCooldown = 0; // Start immediately
        _toolCallback = command.OnComplete;
        _toolCommandId = command.Id;
        _toolName = player.CurrentTool.Name;

        _monitor.Log($"Starting {count} uses of {_toolName}", LogLevel.Info);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Using {_toolName} {count} times"
        };
    }

    private CommandResponse ExecuteSwitchTool(GameCommand command)
    {
        if (!command.Params.TryGetValue("slot", out var slotObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing slot parameter"
            };
        }

        int slot = GetIntParam(slotObj);

        if (slot < 0 || slot > 11)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Slot must be between 0 and 11"
            };
        }

        Game1.player.CurrentToolIndex = slot;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Switched to slot {slot}"
        };
    }

    private CommandResponse ExecuteGetState(GameCommand command)
    {
        // This is handled by the WebSocket server directly
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "State retrieved"
        };
    }

    private CommandResponse ExecuteFaceDirection(GameCommand command)
    {
        if (!command.Params.TryGetValue("direction", out var dirObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing direction parameter"
            };
        }

        string direction = GetStringParam(dirObj).ToLower();
        var player = Game1.player;

        int facingDirection = direction switch
        {
            "up" => 0,
            "right" => 1,
            "down" => 2,
            "left" => 3,
            _ => -1
        };

        if (facingDirection == -1)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Invalid direction: {direction}. Use up, down, left, or right."
            };
        }

        player.FacingDirection = facingDirection;
        player.FarmerSprite.StopAnimation();

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Now facing {direction}",
            Data = new Dictionary<string, object>
            {
                ["direction"] = direction,
                ["facingDirection"] = facingDirection
            }
        };
    }

    private CommandResponse ExecutePlaceItem(GameCommand command)
    {
        var player = Game1.player;

        // Optionally switch to a specific slot first
        if (command.Params.TryGetValue("slot", out var slotObj))
        {
            int slot = GetIntParam(slotObj);
            if (slot >= 0 && slot <= 11)
            {
                player.CurrentToolIndex = slot;
            }
        }

        var currentItem = player.CurrentItem;
        if (currentItem == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No item selected to place"
            };
        }

        // Get the tile in front of the player
        var tileInFront = player.GetToolLocation() / 64f;
        int tileX = (int)tileInFront.X;
        int tileY = (int)tileInFront.Y;
        var location = Game1.currentLocation;

        // Try to place the object
        // For seeds/fertilizer on tilled soil, use the action button
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Attempted to place {currentItem.Name} at ({tileX}, {tileY})",
            Data = new Dictionary<string, object>
            {
                ["item"] = currentItem.Name,
                ["x"] = tileX,
                ["y"] = tileY
            }
        };
    }

    private CommandResponse ExecuteHoldTool(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool == null)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No tool equipped"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = "No tool equipped" };
        }

        // Get duration parameter (default to 60 ticks = 1 second)
        int ticks = 60;
        if (command.Params.TryGetValue("ticks", out var ticksObj))
        {
            ticks = GetIntParam(ticksObj, 60);
        }

        // Clamp ticks to reasonable range (1 second to 3 seconds)
        ticks = Math.Clamp(ticks, 30, 180);

        // Set up hold tool state
        _holdToolTicks = ticks;
        _holdToolRemaining = ticks;
        _holdToolCallback = command.OnComplete;
        _holdToolCommandId = command.Id;
        _holdToolName = player.CurrentTool.Name;
        _holdToolReleased = false;

        _monitor.Log($"Starting {ticks} tick hold of {_holdToolName}", LogLevel.Info);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Holding {_holdToolName} for {ticks} ticks"
        };
    }

    private CommandResponse ExecuteEatItem(GameCommand command)
    {
        var player = Game1.player;

        // Get slot parameter
        if (!command.Params.TryGetValue("slot", out var slotObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing slot parameter"
            };
        }

        int slot = GetIntParam(slotObj);
        if (slot < 0 || slot > 35)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Slot must be between 0 and 35"
            };
        }

        var item = player.Items[slot];
        if (item == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"No item in slot {slot}"
            };
        }

        // Check if item is edible
        if (item is not StardewValley.Object obj || obj.Edibility <= -300)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"{item.Name} is not edible"
            };
        }

        // Switch to the item and eat it
        int previousSlot = player.CurrentToolIndex;
        player.CurrentToolIndex = slot;

        // Press action button to eat
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Eating {item.Name}",
            Data = new Dictionary<string, object>
            {
                ["item"] = item.Name,
                ["edibility"] = obj.Edibility,
                ["slot"] = slot
            }
        };
    }

    private CommandResponse ExecuteSelectItem(GameCommand command)
    {
        if (!command.Params.TryGetValue("name", out var nameObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing name parameter"
            };
        }

        string itemName = GetStringParam(nameObj).ToLower();
        var player = Game1.player;

        // Search inventory for the item
        for (int i = 0; i < player.Items.Count; i++)
        {
            var item = player.Items[i];
            if (item != null && item.Name.ToLower().Contains(itemName))
            {
                player.CurrentToolIndex = i;
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = true,
                    Message = $"Selected {item.Name} in slot {i}",
                    Data = new Dictionary<string, object>
                    {
                        ["item"] = item.Name,
                        ["slot"] = i,
                        ["stack"] = item.Stack
                    }
                };
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = false,
            Message = $"No item matching '{itemName}' found in inventory"
        };
    }

    #region Inventory Management Commands

    private CommandResponse ExecuteTrashItem(GameCommand command)
    {
        if (!command.Params.TryGetValue("slot", out var slotObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing slot parameter"
            };
        }

        int slot = GetIntParam(slotObj);
        var player = Game1.player;

        if (slot < 0 || slot >= player.Items.Count)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Invalid slot {slot}"
            };
        }

        var item = player.Items[slot];
        if (item == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"No item in slot {slot}"
            };
        }

        string itemName = item.Name;
        player.Items[slot] = null;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Trashed {itemName} from slot {slot}"
        };
    }

    private CommandResponse ExecuteShipItem(GameCommand command)
    {
        if (!command.Params.TryGetValue("slot", out var slotObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing slot parameter"
            };
        }

        int slot = GetIntParam(slotObj);
        var player = Game1.player;

        if (slot < 0 || slot >= player.Items.Count)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Invalid slot {slot}"
            };
        }

        var item = player.Items[slot];
        if (item == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"No item in slot {slot}"
            };
        }

        // Check if item can be shipped
        if (item is not StardewValley.Object obj || !obj.canBeShipped())
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"{item.Name} cannot be shipped"
            };
        }

        var farm = Game1.getFarm();
        int salePrice = obj.sellToStorePrice();
        string itemName = item.Name;
        int stack = item.Stack;

        farm.getShippingBin(player).Add(item);
        player.Items[slot] = null;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Shipped {stack}x {itemName} for {salePrice * stack}g",
            Data = new Dictionary<string, object>
            {
                ["item"] = itemName,
                ["quantity"] = stack,
                ["pricePerUnit"] = salePrice,
                ["totalValue"] = salePrice * stack
            }
        };
    }

    #endregion

    #region Fishing Commands

    private CommandResponse ExecuteCastFishingRod(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool is not StardewValley.Tools.FishingRod rod)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No fishing rod equipped. Use select_item to equip a fishing rod first."
            };
        }

        // Get optional power parameter (0-100, default 75)
        int power = 75;
        if (command.Params.TryGetValue("power", out var powerObj))
        {
            power = Math.Clamp(GetIntParam(powerObj, 75), 10, 100);
        }

        // Check if facing water
        var tileInFront = GetTileInFrontOfPlayer();
        if (!Game1.currentLocation.isWaterTile((int)tileInFront.X, (int)tileInFront.Y))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Not facing water. Move to water's edge and face the water first."
            };
        }

        // Set cursor position for casting direction
        SetCursorToFacingTile();

        // Simulate holding and releasing the use button to cast
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Casting fishing rod with {power}% power. Wait for a bite (exclamation mark), then use reel_fish.",
            Data = new Dictionary<string, object>
            {
                ["power"] = power,
                ["facingWater"] = true
            }
        };
    }

    private CommandResponse ExecuteReelFish(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool is not StardewValley.Tools.FishingRod rod)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No fishing rod equipped"
            };
        }

        // Check if we have a bite
        if (!rod.isNibbling)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No fish on the line. Wait for a bite (exclamation mark appears)."
            };
        }

        // Press button to start the minigame
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Reeling in fish! The fishing minigame will start. Keep the fish in the green bar by pressing/releasing the action button.",
            Data = new Dictionary<string, object>
            {
                ["minigameStarted"] = true,
                ["tip"] = "Press action button to raise the bar, release to let it fall"
            }
        };
    }

    private Vector2 GetTileInFrontOfPlayer()
    {
        var player = Game1.player;
        int x = (int)player.Tile.X;
        int y = (int)player.Tile.Y;

        switch (player.FacingDirection)
        {
            case 0: y--; break; // up
            case 1: x++; break; // right
            case 2: y++; break; // down
            case 3: x--; break; // left
        }

        return new Vector2(x, y);
    }

    #endregion

    #region Shopping Commands

    private CommandResponse ExecuteOpenShopMenu(GameCommand command)
    {
        // This simulates interacting with a shopkeeper NPC
        // The player must be standing in front of a shop counter or NPC
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Attempting to open shop menu. Make sure you're facing a shopkeeper or counter."
        };
    }

    private CommandResponse ExecuteBuyItem(GameCommand command)
    {
        // Check if shop menu is open
        if (Game1.activeClickableMenu is not StardewValley.Menus.ShopMenu shopMenu)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No shop menu is open. Use open_shop_menu first while facing a shopkeeper."
            };
        }

        if (!command.Params.TryGetValue("item_name", out var nameObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing item_name parameter"
            };
        }

        string itemName = GetStringParam(nameObj).ToLower();
        int quantity = 1;
        if (command.Params.TryGetValue("quantity", out var qtyObj))
        {
            quantity = Math.Max(1, GetIntParam(qtyObj, 1));
        }

        // Search for item in shop
        foreach (var item in shopMenu.forSale)
        {
            if (item.Name.ToLower().Contains(itemName))
            {
                int price = shopMenu.itemPriceAndStock[item].Price;
                int totalCost = price * quantity;

                if (Game1.player.Money < totalCost)
                {
                    return new CommandResponse
                    {
                        Id = command.Id,
                        Success = false,
                        Message = $"Not enough money. Need {totalCost}g but only have {Game1.player.Money}g"
                    };
                }

                // Found the item - return info for manual purchase via clicking
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = true,
                    Message = $"Found {item.Name} in shop for {price}g each. Total cost for {quantity}: {totalCost}g. Click the item in the shop menu to purchase.",
                    Data = new Dictionary<string, object>
                    {
                        ["item"] = item.Name,
                        ["price"] = price,
                        ["quantity"] = quantity,
                        ["totalCost"] = totalCost,
                        ["canAfford"] = Game1.player.Money >= totalCost,
                        ["playerMoney"] = Game1.player.Money
                    }
                };
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = false,
            Message = $"Item '{itemName}' not found in this shop"
        };
    }

    private CommandResponse ExecuteSellItem(GameCommand command)
    {
        if (Game1.activeClickableMenu is not StardewValley.Menus.ShopMenu shopMenu)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No shop menu is open"
            };
        }

        if (!command.Params.TryGetValue("slot", out var slotObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing slot parameter"
            };
        }

        int slot = GetIntParam(slotObj);
        var player = Game1.player;

        if (slot < 0 || slot >= player.Items.Count || player.Items[slot] == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"No item in slot {slot}"
            };
        }

        var item = player.Items[slot];
        if (item is not StardewValley.Object obj)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"{item.Name} cannot be sold"
            };
        }

        int sellPrice = obj.sellToStorePrice();
        string itemName = item.Name;
        int stack = item.Stack;

        player.Money += sellPrice * stack;
        player.Items[slot] = null;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Sold {stack}x {itemName} for {sellPrice * stack}g",
            Data = new Dictionary<string, object>
            {
                ["item"] = itemName,
                ["quantity"] = stack,
                ["earned"] = sellPrice * stack,
                ["totalMoney"] = player.Money
            }
        };
    }

    #endregion

    #region Social Commands

    private CommandResponse ExecuteGiveGift(GameCommand command)
    {
        var player = Game1.player;
        var currentItem = player.CurrentItem;

        if (currentItem == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No item selected to give as gift. Use select_item first."
            };
        }

        // Find NPC in front of player
        var tileInFront = GetTileInFrontOfPlayer();
        NPC? targetNpc = null;

        foreach (var npc in Game1.currentLocation.characters)
        {
            if ((int)npc.Tile.X == (int)tileInFront.X && (int)npc.Tile.Y == (int)tileInFront.Y)
            {
                targetNpc = npc;
                break;
            }
        }

        if (targetNpc == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No NPC in front of you. Move next to an NPC and face them."
            };
        }

        // Check if we've already given a gift today
        if (player.friendshipData.ContainsKey(targetNpc.Name))
        {
            var friendship = player.friendshipData[targetNpc.Name];
            if (friendship.GiftsToday >= 1)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Already gave {targetNpc.Name} a gift today"
                };
            }
            if (friendship.GiftsThisWeek >= 2)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Already gave {targetNpc.Name} 2 gifts this week"
                };
            }
        }

        // Give the gift by pressing action button
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Giving {currentItem.Name} to {targetNpc.Name}",
            Data = new Dictionary<string, object>
            {
                ["item"] = currentItem.Name,
                ["npc"] = targetNpc.Name,
                ["isBirthday"] = targetNpc.isBirthday()
            }
        };
    }

    private CommandResponse ExecuteCheckMail(GameCommand command)
    {
        var player = Game1.player;

        if (player.mailbox.Count == 0)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No mail in mailbox"
            };
        }

        // Get first mail item
        string mailId = player.mailbox[0];

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"You have {player.mailbox.Count} mail items. Interact with your mailbox to read them.",
            Data = new Dictionary<string, object>
            {
                ["mailCount"] = player.mailbox.Count,
                ["firstMail"] = mailId
            }
        };
    }

    #endregion

    #region Crafting Commands

    private CommandResponse ExecuteCraftItem(GameCommand command)
    {
        if (!command.Params.TryGetValue("recipe", out var recipeObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing recipe parameter"
            };
        }

        string recipeName = GetStringParam(recipeObj);
        var player = Game1.player;

        // Check if player knows the recipe
        if (!player.craftingRecipes.ContainsKey(recipeName))
        {
            // Try partial match
            string? matchedRecipe = null;
            foreach (var known in player.craftingRecipes.Keys)
            {
                if (known.ToLower().Contains(recipeName.ToLower()))
                {
                    matchedRecipe = known;
                    break;
                }
            }

            if (matchedRecipe == null)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Recipe '{recipeName}' not known. Check player's known recipes."
                };
            }
            recipeName = matchedRecipe;
        }

        var recipe = new CraftingRecipe(recipeName);

        // Check if we have ingredients
        if (!recipe.doesFarmerHaveIngredientsInInventory())
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Missing ingredients for {recipeName}",
                Data = new Dictionary<string, object>
                {
                    ["recipe"] = recipeName,
                    ["description"] = recipe.description
                }
            };
        }

        // Craft the item
        recipe.consumeIngredients(null);
        var craftedItem = recipe.createItem();
        player.addItemToInventory(craftedItem);
        player.craftingRecipes[recipeName]++;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Crafted {craftedItem.Name}",
            Data = new Dictionary<string, object>
            {
                ["item"] = craftedItem.Name,
                ["quantity"] = craftedItem.Stack
            }
        };
    }

    #endregion

    #region Navigation Commands

    private CommandResponse ExecuteWarpToLocation(GameCommand command)
    {
        if (!command.Params.TryGetValue("location", out var locObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing location parameter"
            };
        }

        string locationName = GetStringParam(locObj);

        // Get the target location
        var targetLocation = Game1.getLocationFromName(locationName);
        if (targetLocation == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Location '{locationName}' not found",
                Data = new Dictionary<string, object>
                {
                    ["hint"] = "Common locations: Farm, FarmHouse, Town, Beach, Mountain, Forest, Mine, BusStop"
                }
            };
        }

        // Find a valid warp point or default spawn
        int targetX = 0, targetY = 0;
        if (targetLocation.warps.Count > 0)
        {
            // Use first warp's target position as entry point
            var firstWarp = targetLocation.warps[0];
            targetX = firstWarp.TargetX;
            targetY = firstWarp.TargetY;
        }

        // Note: Direct warping should be used carefully - usually AI should walk
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"To travel to {locationName}, find and use the appropriate warp point or path. Direct warping is not recommended for gameplay.",
            Data = new Dictionary<string, object>
            {
                ["location"] = locationName,
                ["suggestion"] = "Use move_to and enter_door commands to travel naturally"
            }
        };
    }

    private CommandResponse ExecuteEnterDoor(GameCommand command)
    {
        // Press action button to interact with door/warp in front of player
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        var tileInFront = GetTileInFrontOfPlayer();

        // Check if there's a warp at this position
        bool isWarp = false;
        string targetLocation = "";

        foreach (var warp in Game1.currentLocation.warps)
        {
            if (warp.X == (int)tileInFront.X && warp.Y == (int)tileInFront.Y)
            {
                isWarp = true;
                targetLocation = warp.TargetName;
                break;
            }
        }

        if (!isWarp)
        {
            // Check doors
            if (Game1.currentLocation.doors.ContainsKey(new Microsoft.Xna.Framework.Point((int)tileInFront.X, (int)tileInFront.Y)))
            {
                isWarp = true;
                targetLocation = "interior";
            }
        }

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = isWarp ? $"Entering door to {targetLocation}" : "Attempting to enter door/warp at current position",
            Data = new Dictionary<string, object>
            {
                ["tileX"] = (int)tileInFront.X,
                ["tileY"] = (int)tileInFront.Y,
                ["isWarp"] = isWarp,
                ["target"] = targetLocation
            }
        };
    }

    #endregion

    #region Combat Commands

    private CommandResponse ExecuteAttack(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool is not StardewValley.Tools.MeleeWeapon weapon)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No weapon equipped. Use equip_weapon or select_item to equip a weapon first."
            };
        }

        // Swing the weapon
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        SetCursorToFacingTile();
        _helper.Input.Press(useButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Attacking with {weapon.Name}",
            Data = new Dictionary<string, object>
            {
                ["weapon"] = weapon.Name,
                ["facingDirection"] = player.FacingDirection
            }
        };
    }

    private CommandResponse ExecuteEquipWeapon(GameCommand command)
    {
        var player = Game1.player;

        // Find first weapon in inventory
        for (int i = 0; i < player.Items.Count; i++)
        {
            if (player.Items[i] is StardewValley.Tools.MeleeWeapon weapon)
            {
                player.CurrentToolIndex = i;
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = true,
                    Message = $"Equipped {weapon.Name} from slot {i}",
                    Data = new Dictionary<string, object>
                    {
                        ["weapon"] = weapon.Name,
                        ["slot"] = i,
                        ["damage"] = weapon.minDamage.Value + "-" + weapon.maxDamage.Value
                    }
                };
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = false,
            Message = "No weapon found in inventory"
        };
    }

    #endregion

    #region Animal Commands

    private CommandResponse ExecutePetAnimal(GameCommand command)
    {
        var player = Game1.player;
        var tileInFront = GetTileInFrontOfPlayer();
        var location = Game1.currentLocation;

        // Find animal at tile in front
        FarmAnimal? animal = null;

        if (location is Farm farm)
        {
            foreach (var a in farm.animals.Values)
            {
                if ((int)a.Tile.X == (int)tileInFront.X && (int)a.Tile.Y == (int)tileInFront.Y)
                {
                    animal = a;
                    break;
                }
            }
        }
        else if (location is AnimalHouse animalHouse)
        {
            foreach (var a in animalHouse.animals.Values)
            {
                if ((int)a.Tile.X == (int)tileInFront.X && (int)a.Tile.Y == (int)tileInFront.Y)
                {
                    animal = a;
                    break;
                }
            }
        }

        if (animal == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No animal in front of you"
            };
        }

        if (animal.wasPet.Value)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"{animal.Name} has already been pet today"
            };
        }

        // Pet the animal via interact
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Petting {animal.Name} the {animal.type.Value}",
            Data = new Dictionary<string, object>
            {
                ["animalName"] = animal.Name,
                ["animalType"] = animal.type.Value,
                ["happiness"] = animal.happiness.Value
            }
        };
    }

    private CommandResponse ExecuteMilkAnimal(GameCommand command)
    {
        var player = Game1.player;

        // Check if holding milk pail
        if (player.CurrentTool is not StardewValley.Tools.MilkPail)
        {
            // Try to find and equip milk pail
            for (int i = 0; i < player.Items.Count; i++)
            {
                if (player.Items[i] is StardewValley.Tools.MilkPail)
                {
                    player.CurrentToolIndex = i;
                    break;
                }
            }

            if (player.CurrentTool is not StardewValley.Tools.MilkPail)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = "No milk pail in inventory. Buy one from Marnie's shop."
                };
            }
        }

        // Use the tool
        SetCursorToFacingTile();
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Using milk pail on animal in front"
        };
    }

    private CommandResponse ExecuteShearAnimal(GameCommand command)
    {
        var player = Game1.player;

        // Check if holding shears
        if (player.CurrentTool is not StardewValley.Tools.Shears)
        {
            // Try to find and equip shears
            for (int i = 0; i < player.Items.Count; i++)
            {
                if (player.Items[i] is StardewValley.Tools.Shears)
                {
                    player.CurrentToolIndex = i;
                    break;
                }
            }

            if (player.CurrentTool is not StardewValley.Tools.Shears)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = "No shears in inventory. Buy them from Marnie's shop."
                };
            }
        }

        // Use the tool
        SetCursorToFacingTile();
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Using shears on animal in front"
        };
    }

    private CommandResponse ExecuteCollectProduct(GameCommand command)
    {
        // Collect eggs, truffles, or other animal products
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Attempting to collect product from tile in front"
        };
    }

    #endregion

    #region Mining Commands

    private CommandResponse ExecuteUseBomb(GameCommand command)
    {
        var player = Game1.player;

        // Find bomb in inventory
        int bombSlot = -1;
        string bombType = "";

        for (int i = 0; i < player.Items.Count; i++)
        {
            var item = player.Items[i];
            if (item != null && (item.Name.Contains("Bomb") || item.Name.Contains("Cherry Bomb") || item.Name.Contains("Mega Bomb")))
            {
                bombSlot = i;
                bombType = item.Name;
                break;
            }
        }

        if (bombSlot == -1)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No bombs in inventory. Craft or buy bombs first."
            };
        }

        // Select and place the bomb
        player.CurrentToolIndex = bombSlot;

        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Placing {bombType}. Move away before it explodes!",
            Data = new Dictionary<string, object>
            {
                ["bombType"] = bombType,
                ["warning"] = "Move at least 3 tiles away within 4 seconds!"
            }
        };
    }

    #endregion

    #region Cheat Mode Commands

    private CommandResponse ExecuteCheatModeEnable(GameCommand command)
    {
        _cheatModeEnabled = true;
        _monitor.Log("Cheat mode ENABLED", LogLevel.Warn);
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Cheat mode enabled. All cheat commands are now available."
        };
    }

    private CommandResponse ExecuteCheatModeDisable(GameCommand command)
    {
        _cheatModeEnabled = false;
        _monitor.Log("Cheat mode DISABLED", LogLevel.Info);
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Cheat mode disabled."
        };
    }

    private CommandResponse CheatModeDisabledResponse(GameCommand command) => new()
    {
        Id = command.Id,
        Success = false,
        Message = "Cheat mode is disabled. Use cheat_mode_enable first."
    };

    private CommandResponse ExecuteCheatWarp(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("location", out var locObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: location"
            };
        }

        var locationName = GetStringParam(locObj);
        var x = command.Params.TryGetValue("x", out var xObj) ? GetIntParam(xObj, -1) : -1;
        var y = command.Params.TryGetValue("y", out var yObj) ? GetIntParam(yObj, -1) : -1;

        // Validate location exists
        var targetLocation = Game1.getLocationFromName(locationName);
        if (targetLocation == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Unknown location: {locationName}. Use location names like Farm, Town, Mountain, Beach, Forest, etc."
            };
        }

        // Use default spawn point if coordinates not specified
        if (x < 0 || y < 0)
        {
            // Try to get a reasonable spawn point
            var warpPoints = targetLocation.warps;
            if (warpPoints != null && warpPoints.Count > 0)
            {
                x = warpPoints[0].X;
                y = warpPoints[0].Y;
            }
            else
            {
                // Default to center-ish of map
                x = targetLocation.Map.Layers[0].LayerWidth / 2;
                y = targetLocation.Map.Layers[0].LayerHeight / 2;
            }
        }

        Game1.warpFarmer(locationName, x, y, false);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Warped to {locationName} at ({x}, {y})",
            Data = new Dictionary<string, object>
            {
                ["location"] = locationName,
                ["x"] = x,
                ["y"] = y
            }
        };
    }

    private CommandResponse ExecuteCheatSetMoney(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("amount", out var amountObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: amount"
            };
        }

        var amount = GetIntParam(amountObj);
        if (amount < 0) amount = 0;

        var oldMoney = Game1.player.Money;
        Game1.player.Money = amount;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Money set from {oldMoney}g to {amount}g",
            Data = new Dictionary<string, object>
            {
                ["oldMoney"] = oldMoney,
                ["newMoney"] = amount
            }
        };
    }

    private CommandResponse ExecuteCheatAddItem(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("itemId", out var itemIdObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: itemId (e.g., '(O)465' for Parsnip Seeds)"
            };
        }

        var itemId = GetStringParam(itemIdObj);
        var count = command.Params.TryGetValue("count", out var countObj) ? GetIntParam(countObj, 1) : 1;
        var quality = command.Params.TryGetValue("quality", out var qualObj) ? GetIntParam(qualObj, 0) : 0;

        if (count < 1) count = 1;
        if (count > 999) count = 999;
        if (quality < 0) quality = 0;
        if (quality > 4) quality = 4; // 0=normal, 1=silver, 2=gold, 4=iridium

        try
        {
            // Create the item using ItemRegistry (Stardew 1.6+ style)
            var item = ItemRegistry.Create(itemId, count, quality);
            if (item == null)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Failed to create item with ID: {itemId}. Use format like '(O)472' for objects, '(T)Pickaxe' for tools."
                };
            }

            var added = Game1.player.addItemToInventory(item);
            if (added == null)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = true,
                    Message = $"Added {count}x {item.DisplayName} (quality {quality}) to inventory",
                    Data = new Dictionary<string, object>
                    {
                        ["itemId"] = itemId,
                        ["itemName"] = item.DisplayName,
                        ["count"] = count,
                        ["quality"] = quality
                    }
                };
            }
            else
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Inventory full. Could not add {item.DisplayName}."
                };
            }
        }
        catch (Exception ex)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Error creating item: {ex.Message}"
            };
        }
    }

    private CommandResponse ExecuteCheatSetEnergy(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var amount = command.Params.TryGetValue("amount", out var amountObj)
            ? GetIntParam(amountObj, (int)Game1.player.MaxStamina)
            : (int)Game1.player.MaxStamina;

        var oldEnergy = Game1.player.Stamina;
        Game1.player.Stamina = Math.Min(amount, Game1.player.MaxStamina);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Energy set from {oldEnergy:F0} to {Game1.player.Stamina:F0} (max: {Game1.player.MaxStamina})",
            Data = new Dictionary<string, object>
            {
                ["oldEnergy"] = oldEnergy,
                ["newEnergy"] = Game1.player.Stamina,
                ["maxEnergy"] = Game1.player.MaxStamina
            }
        };
    }

    private CommandResponse ExecuteCheatSetHealth(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var amount = command.Params.TryGetValue("amount", out var amountObj)
            ? GetIntParam(amountObj, Game1.player.maxHealth)
            : Game1.player.maxHealth;

        var oldHealth = Game1.player.health;
        Game1.player.health = Math.Min(amount, Game1.player.maxHealth);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Health set from {oldHealth} to {Game1.player.health} (max: {Game1.player.maxHealth})",
            Data = new Dictionary<string, object>
            {
                ["oldHealth"] = oldHealth,
                ["newHealth"] = Game1.player.health,
                ["maxHealth"] = Game1.player.maxHealth
            }
        };
    }

    private CommandResponse ExecuteCheatAddExperience(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("skillId", out var skillIdObj) ||
            !command.Params.TryGetValue("amount", out var amountObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameters: skillId (0-4) and amount. Skills: 0=Farming, 1=Fishing, 2=Foraging, 3=Mining, 4=Combat"
            };
        }

        var skillId = GetIntParam(skillIdObj);
        var amount = GetIntParam(amountObj);

        if (skillId < 0 || skillId > 4)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Invalid skillId. Must be 0-4: 0=Farming, 1=Fishing, 2=Foraging, 3=Mining, 4=Combat"
            };
        }

        var skillNames = new[] { "Farming", "Fishing", "Foraging", "Mining", "Combat" };
        var oldLevel = Game1.player.GetSkillLevel(skillId);
        var oldExp = Game1.player.experiencePoints[skillId];

        Game1.player.gainExperience(skillId, amount);

        var newLevel = Game1.player.GetSkillLevel(skillId);
        var newExp = Game1.player.experiencePoints[skillId];

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Added {amount} XP to {skillNames[skillId]}. Level: {oldLevel} -> {newLevel}, XP: {oldExp} -> {newExp}",
            Data = new Dictionary<string, object>
            {
                ["skill"] = skillNames[skillId],
                ["skillId"] = skillId,
                ["xpAdded"] = amount,
                ["oldLevel"] = oldLevel,
                ["newLevel"] = newLevel,
                ["oldXp"] = oldExp,
                ["newXp"] = newExp
            }
        };
    }

    private CommandResponse ExecuteCheatHarvestAll(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var harvestedCount = 0;
        var notReadyCount = 0;
        var deadCount = 0;

        foreach (var pair in location.terrainFeatures.Pairs.ToList())
        {
            if (pair.Value is HoeDirt hoeDirt && hoeDirt.crop != null)
            {
                var crop = hoeDirt.crop;
                var tileX = (int)pair.Key.X;
                var tileY = (int)pair.Key.Y;

                if (crop.dead.Value)
                {
                    deadCount++;
                    continue;
                }

                // Check if crop is ready to harvest
                bool isReady = crop.phaseDays.Count > 0 && crop.currentPhase.Value >= crop.phaseDays.Count - 1;

                if (isReady)
                {
                    if (crop.harvest(tileX, tileY, hoeDirt, null))
                    {
                        harvestedCount++;
                        if (!crop.RegrowsAfterHarvest())
                        {
                            hoeDirt.crop = null;
                        }
                    }
                }
                else
                {
                    notReadyCount++;
                }
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Harvested {harvestedCount} crops in {location.Name}" + 
                      (notReadyCount > 0 ? $" ({notReadyCount} not ready)" : "") +
                      (deadCount > 0 ? $" ({deadCount} dead)" : ""),
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["harvested"] = harvestedCount,
                ["notReady"] = notReadyCount,
                ["dead"] = deadCount
            }
        };
    }

    private CommandResponse ExecuteCheatWaterAll(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var wateredCount = 0;

        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is HoeDirt hoeDirt)
            {
                if (hoeDirt.state.Value != 1) // Not already watered
                {
                    hoeDirt.state.Value = 1; // 1 = watered
                    wateredCount++;
                }
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Watered {wateredCount} soil tiles in {location.Name}",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["watered"] = wateredCount
            }
        };
    }

    private CommandResponse ExecuteCheatGrowCrops(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var grownCount = 0;
        var deadCount = 0;
        var alreadyGrownCount = 0;

        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is HoeDirt hoeDirt && hoeDirt.crop != null)
            {
                var crop = hoeDirt.crop;
                
                // Revive dead crops first
                if (crop.dead.Value)
                {
                    crop.dead.Value = false;
                    deadCount++;
                }
                
                // Check if crop has growth phases
                if (crop.phaseDays.Count > 0)
                {
                    // If not at final phase, grow it
                    if (crop.currentPhase.Value < crop.phaseDays.Count - 1)
                    {
                        crop.currentPhase.Value = crop.phaseDays.Count - 1;
                        crop.dayOfCurrentPhase.Value = 0;
                        grownCount++;
                    }
                    else
                    {
                        alreadyGrownCount++;
                    }
                }
                else
                {
                    // Crop with no phase data - just mark it as grown
                    grownCount++;
                }
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Grew {grownCount} crops in {location.Name} (revived {deadCount} dead, {alreadyGrownCount} already mature)",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["grown"] = grownCount,
                ["revived"] = deadCount,
                ["alreadyMature"] = alreadyGrownCount
            }
        };
    }

    private CommandResponse ExecuteCheatClearDebris(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var clearedObjects = 0;
        var clearedFeatures = 0;

        // Clear debris objects (weeds, stones, twigs, fiber)
        var debrisIds = new HashSet<string>
        {
            "(O)0", "(O)2", "(O)4", "(O)6", "(O)8", "(O)10", "(O)12", "(O)14", "(O)16", // Weeds
            "(O)294", "(O)295", "(O)313", "(O)314", "(O)315", "(O)316", "(O)317", "(O)318", "(O)319", // More weeds
            "(O)343", "(O)450", "(O)668", "(O)670", "(O)784", "(O)785", "(O)786", // Stones
            "(O)751", "(O)290", "(O)289", // Copper/Iron/Gold nodes (optional)
            "(O)294", "(O)295", // Twig types
        };

        // Also check by name patterns
        var objectsToRemove = new List<Vector2>();
        foreach (var pair in location.Objects.Pairs)
        {
            var obj = pair.Value;
            var name = obj.Name?.ToLower() ?? "";
            var qualifiedId = obj.QualifiedItemId ?? "";

            if (name.Contains("weed") || name.Contains("stone") || name.Contains("twig") ||
                name == "fiber" || debrisIds.Contains(qualifiedId))
            {
                objectsToRemove.Add(pair.Key);
            }
        }

        foreach (var pos in objectsToRemove)
        {
            location.Objects.Remove(pos);
            clearedObjects++;
        }

        // Clear terrain features like grass
        var featuresToRemove = new List<Vector2>();
        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is Grass)
            {
                featuresToRemove.Add(pair.Key);
            }
        }

        foreach (var pos in featuresToRemove)
        {
            location.terrainFeatures.Remove(pos);
            clearedFeatures++;
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Cleared {clearedObjects} debris objects and {clearedFeatures} grass patches in {location.Name}",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["clearedObjects"] = clearedObjects,
                ["clearedGrass"] = clearedFeatures
            }
        };
    }

    private CommandResponse ExecuteCheatMineWarp(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("level", out var levelObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: level (1-120 for Mines, 121+ for Skull Cavern, 77377 for Quarry Mine)"
            };
        }

        var level = GetIntParam(levelObj);

        if (level < 1)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Invalid level. Use 1-120 for Mines, 121+ for Skull Cavern, or 77377 for Quarry Mine."
            };
        }

        Game1.enterMine(level);

        var mineType = level == 77377 ? "Quarry Mine" : (level <= 120 ? "Mines" : "Skull Cavern");

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Warped to {mineType} level {level}",
            Data = new Dictionary<string, object>
            {
                ["mineType"] = mineType,
                ["level"] = level
            }
        };
    }

    private CommandResponse ExecuteCheatSpawnOres(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("oreType", out var oreTypeObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: oreType (copper, iron, gold, iridium, coal)"
            };
        }

        var oreType = GetStringParam(oreTypeObj).ToLower();
        var count = command.Params.TryGetValue("count", out var countObj) ? GetIntParam(countObj, 10) : 10;

        if (count < 1) count = 1;
        if (count > 999) count = 999;

        // Map ore type to item ID
        var oreItemIds = new Dictionary<string, (string id, string name)>
        {
            ["copper"] = ("(O)378", "Copper Ore"),
            ["iron"] = ("(O)380", "Iron Ore"),
            ["gold"] = ("(O)384", "Gold Ore"),
            ["iridium"] = ("(O)386", "Iridium Ore"),
            ["coal"] = ("(O)382", "Coal"),
            ["stone"] = ("(O)390", "Stone"),
            ["geode"] = ("(O)535", "Geode"),
            ["frozen_geode"] = ("(O)536", "Frozen Geode"),
            ["magma_geode"] = ("(O)537", "Magma Geode"),
            ["omni_geode"] = ("(O)749", "Omni Geode"),
        };

        if (!oreItemIds.TryGetValue(oreType, out var oreInfo))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Unknown ore type: {oreType}. Valid types: {string.Join(", ", oreItemIds.Keys)}"
            };
        }

        try
        {
            var item = ItemRegistry.Create(oreInfo.id, count);
            var added = Game1.player.addItemToInventory(item);

            if (added == null)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = true,
                    Message = $"Added {count}x {oreInfo.name} to inventory",
                    Data = new Dictionary<string, object>
                    {
                        ["oreType"] = oreType,
                        ["itemName"] = oreInfo.name,
                        ["count"] = count
                    }
                };
            }
            else
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Inventory full. Could not add {oreInfo.name}."
                };
            }
        }
        catch (Exception ex)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Error spawning ore: {ex.Message}"
            };
        }
    }

    private CommandResponse ExecuteCheatSetFriendship(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("npcName", out var npcNameObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: npcName"
            };
        }

        var npcName = GetStringParam(npcNameObj);
        
        // Support both "points" (raw) and "hearts" (converted)
        int points;
        if (command.Params.TryGetValue("hearts", out var heartsObj))
        {
            var hearts = GetIntParam(heartsObj);
            points = hearts * 250; // 250 points per heart
        }
        else if (command.Params.TryGetValue("points", out var pointsObj))
        {
            points = GetIntParam(pointsObj);
        }
        else
        {
            points = 2500; // Default to 10 hearts
        }

        // Find the NPC
        NPC? targetNpc = null;
        foreach (var location in Game1.locations)
        {
            targetNpc = location.getCharacterFromName(npcName);
            if (targetNpc != null) break;
        }

        if (targetNpc == null)
        {
            // Try case-insensitive match
            var npcNameLower = npcName.ToLower();
            foreach (var location in Game1.locations)
            {
                foreach (var npc in location.characters)
                {
                    if (npc.Name.ToLower() == npcNameLower)
                    {
                        targetNpc = npc;
                        break;
                    }
                }
                if (targetNpc != null) break;
            }
        }

        if (targetNpc == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"NPC not found: {npcName}. Make sure to use their exact name (e.g., 'Abigail', 'Sebastian')."
            };
        }

        var player = Game1.player;
        var actualName = targetNpc.Name;

        // Check if already friends
        if (!player.friendshipData.ContainsKey(actualName))
        {
            player.friendshipData.Add(actualName, new Friendship());
        }

        var oldPoints = player.friendshipData[actualName].Points;
        player.friendshipData[actualName].Points = Math.Max(0, points);

        var newHearts = player.friendshipData[actualName].Points / 250;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Friendship with {actualName}: {oldPoints} -> {points} points ({newHearts} hearts)",
            Data = new Dictionary<string, object>
            {
                ["npcName"] = actualName,
                ["oldPoints"] = oldPoints,
                ["newPoints"] = points,
                ["hearts"] = newHearts
            }
        };
    }

    private CommandResponse ExecuteCheatMaxAllFriendships(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var player = Game1.player;
        var updatedCount = 0;
        var maxPoints = 2500; // 10 hearts by default
        
        // Check for 14 hearts (dating/spouse max)
        if (command.Params.TryGetValue("includeRomance", out var romanceObj) && 
            GetStringParam(romanceObj).ToLower() == "true")
        {
            maxPoints = 3500; // 14 hearts for romanceable NPCs
        }

        // Get all NPCs that can have friendship
        var socialNpcs = new List<string>();
        foreach (var location in Game1.locations)
        {
            foreach (var npc in location.characters)
            {
                if (npc.CanSocialize && !socialNpcs.Contains(npc.Name))
                {
                    socialNpcs.Add(npc.Name);
                }
            }
        }

        foreach (var npcName in socialNpcs)
        {
            if (!player.friendshipData.ContainsKey(npcName))
            {
                player.friendshipData.Add(npcName, new Friendship());
            }
            player.friendshipData[npcName].Points = maxPoints;
            updatedCount++;
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Maxed friendship with {updatedCount} NPCs to {maxPoints / 250} hearts",
            Data = new Dictionary<string, object>
            {
                ["npcCount"] = updatedCount,
                ["pointsSet"] = maxPoints,
                ["hearts"] = maxPoints / 250
            }
        };
    }

    private CommandResponse ExecuteCheatCollectAllForage(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var collectedCount = 0;
        var collectedItems = new List<string>();

        // Collect forage objects
        var objectsToRemove = new List<Vector2>();
        foreach (var pair in location.Objects.Pairs)
        {
            var obj = pair.Value;
            // Check if it's a forageable item
            if (obj.IsSpawnedObject || obj.isForage())
            {
                // Add to player inventory
                var item = ItemRegistry.Create(obj.QualifiedItemId, obj.Stack, obj.Quality);
                if (item != null)
                {
                    var added = Game1.player.addItemToInventory(item);
                    if (added == null)
                    {
                        objectsToRemove.Add(pair.Key);
                        collectedCount++;
                        collectedItems.Add(obj.DisplayName);
                    }
                }
            }
        }

        foreach (var pos in objectsToRemove)
        {
            location.Objects.Remove(pos);
        }

        // Also collect artifact spots (worms)
        var artifactSpots = new List<Vector2>();
        foreach (var pair in location.Objects.Pairs)
        {
            if (pair.Value.QualifiedItemId == "(O)590") // Artifact Spot
            {
                artifactSpots.Add(pair.Key);
            }
        }

        foreach (var spot in artifactSpots)
        {
            // Dig up the artifact spot and add random loot
            location.Objects.Remove(spot);
            // Add a random common forage item instead of digging
            var random = Game1.random;
            var commonForage = new[] { "(O)16", "(O)18", "(O)20", "(O)22" }; // Wild Horseradish, Daffodil, Leek, Dandelion
            var forageItem = ItemRegistry.Create(commonForage[random.Next(commonForage.Length)]);
            if (forageItem != null)
            {
                Game1.player.addItemToInventory(forageItem);
                collectedCount++;
                collectedItems.Add(forageItem.DisplayName);
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Collected {collectedCount} forage items in {location.Name}",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["collected"] = collectedCount,
                ["items"] = collectedItems.Take(10).ToList() // Show first 10 items
            }
        };
    }

    private CommandResponse ExecuteCheatInstantMine(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        
        if (location is not MineShaft mine)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Not in a mine. Use cheat_mine_warp first to enter the mines."
            };
        }

        var minedCount = 0;
        var collectedOres = new Dictionary<string, int>();
        var objectsToRemove = new List<Vector2>();

        // Mine all ore nodes and breakable rocks
        foreach (var pair in location.Objects.Pairs.ToList())
        {
            var obj = pair.Value;
            var name = obj.Name?.ToLower() ?? "";
            
            // Check if it's a stone/ore node
            if (obj.Name == "Stone" || name.Contains("ore") || name.Contains("node") ||
                obj.QualifiedItemId.StartsWith("(O)75") || // Various ores
                obj.QualifiedItemId.StartsWith("(O)76") ||
                obj.QualifiedItemId.StartsWith("(O)77") ||
                obj.QualifiedItemId == "(O)290" || // Iron node
                obj.QualifiedItemId == "(O)751" || // Copper node  
                obj.QualifiedItemId == "(O)764" || // Gold node
                obj.QualifiedItemId == "(O)765" || // Iridium node
                obj.QualifiedItemId == "(O)819" || // Omni geode node
                obj.QualifiedItemId == "(O)843" || // Cinder shard node
                obj.QualifiedItemId == "(O)844" || // Cinder shard node
                obj.QualifiedItemId == "(O)95")    // Radioactive node
            {
                objectsToRemove.Add(pair.Key);
                
                // Determine what ore/gems to give based on node type
                var drops = GetMineDrops(obj, mine.mineLevel);
                foreach (var drop in drops)
                {
                    var item = ItemRegistry.Create(drop.itemId, drop.count);
                    if (item != null)
                    {
                        Game1.player.addItemToInventory(item);
                        if (!collectedOres.ContainsKey(item.DisplayName))
                            collectedOres[item.DisplayName] = 0;
                        collectedOres[item.DisplayName] += drop.count;
                    }
                }
                minedCount++;
            }
        }

        foreach (var pos in objectsToRemove)
        {
            location.Objects.Remove(pos);
        }

        // Also break resource clumps (large rocks, etc.)
        var clumpsToRemove = new List<ResourceClump>();
        foreach (var clump in location.resourceClumps.ToList())
        {
            // Give appropriate drops
            var clumpDrops = GetResourceClumpDrops(clump);
            foreach (var drop in clumpDrops)
            {
                var item = ItemRegistry.Create(drop.itemId, drop.count);
                if (item != null)
                {
                    Game1.player.addItemToInventory(item);
                    if (!collectedOres.ContainsKey(item.DisplayName))
                        collectedOres[item.DisplayName] = 0;
                    collectedOres[item.DisplayName] += drop.count;
                }
            }
            clumpsToRemove.Add(clump);
            minedCount++;
        }

        foreach (var clump in clumpsToRemove)
        {
            location.resourceClumps.Remove(clump);
        }

        // Format collected items for display
        var collectedSummary = string.Join(", ", collectedOres.Select(kv => $"{kv.Value}x {kv.Key}"));

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Mined {minedCount} nodes on level {mine.mineLevel}. Collected: {collectedSummary}",
            Data = new Dictionary<string, object>
            {
                ["mineLevel"] = mine.mineLevel,
                ["nodesMined"] = minedCount,
                ["collected"] = collectedOres
            }
        };
    }

    private List<(string itemId, int count)> GetMineDrops(SObject obj, int mineLevel)
    {
        var drops = new List<(string itemId, int count)>();
        var random = new Random();
        
        // Determine drops based on mine level and node type
        if (mineLevel <= 40)
        {
            drops.Add(("(O)378", random.Next(1, 4))); // Copper Ore
            if (random.NextDouble() < 0.1) drops.Add(("(O)535", 1)); // Geode
        }
        else if (mineLevel <= 80)
        {
            drops.Add(("(O)380", random.Next(1, 4))); // Iron Ore
            if (random.NextDouble() < 0.1) drops.Add(("(O)536", 1)); // Frozen Geode
        }
        else if (mineLevel <= 120)
        {
            drops.Add(("(O)384", random.Next(1, 4))); // Gold Ore
            if (random.NextDouble() < 0.1) drops.Add(("(O)537", 1)); // Magma Geode
        }
        else // Skull Cavern
        {
            if (random.NextDouble() < 0.3)
                drops.Add(("(O)386", random.Next(1, 3))); // Iridium Ore
            else
                drops.Add(("(O)384", random.Next(1, 4))); // Gold Ore
            if (random.NextDouble() < 0.05) drops.Add(("(O)749", 1)); // Omni Geode
        }
        
        // Always some stone
        drops.Add(("(O)390", random.Next(1, 3))); // Stone
        
        // Chance for coal
        if (random.NextDouble() < 0.05)
            drops.Add(("(O)382", 1)); // Coal

        return drops;
    }

    private List<(string itemId, int count)> GetResourceClumpDrops(ResourceClump clump)
    {
        var drops = new List<(string itemId, int count)>();
        var random = new Random();

        // Based on clump type
        switch (clump.parentSheetIndex.Value)
        {
            case 672: // Large boulder
                drops.Add(("(O)390", random.Next(10, 20))); // Stone
                break;
            case 752: // Copper boulder
                drops.Add(("(O)378", random.Next(5, 10))); // Copper
                break;
            case 754: // Iron boulder
                drops.Add(("(O)380", random.Next(5, 10))); // Iron
                break;
            case 756: // Gold boulder
                drops.Add(("(O)384", random.Next(5, 10))); // Gold
                break;
            case 758: // Iridium boulder
                drops.Add(("(O)386", random.Next(3, 8))); // Iridium
                break;
            default:
                drops.Add(("(O)390", random.Next(5, 15))); // Stone
                break;
        }

        return drops;
    }

    private CommandResponse ExecuteCheatTimeSet(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("time", out var timeObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: time (e.g., 600 for 6:00 AM, 1800 for 6:00 PM)"
            };
        }

        var time = GetIntParam(timeObj);
        
        // Validate time format (600-2600)
        if (time < 600 || time > 2600)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Invalid time. Use 24-hour format: 600 (6 AM) to 2600 (2 AM next day)"
            };
        }

        var oldTime = Game1.timeOfDay;
        Game1.timeOfDay = time;

        // Format time for display
        var hours = time / 100;
        var minutes = time % 100;
        var ampm = hours >= 12 && hours < 24 ? "PM" : "AM";
        var displayHours = hours > 12 ? hours - 12 : (hours == 0 ? 12 : hours);
        if (hours >= 24) displayHours = hours - 24;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Time set from {oldTime} to {time} ({displayHours}:{minutes:D2} {ampm})",
            Data = new Dictionary<string, object>
            {
                ["oldTime"] = oldTime,
                ["newTime"] = time,
                ["formatted"] = $"{displayHours}:{minutes:D2} {ampm}"
            }
        };
    }

    private CommandResponse ExecuteCheatTimeFreeze(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        _timeFreezeEnabled = !_timeFreezeEnabled;
        
        if (_timeFreezeEnabled)
        {
            _frozenTime = Game1.timeOfDay;
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = _timeFreezeEnabled 
                ? $"Time FROZEN at {Game1.timeOfDay}. Use cheat_time_freeze again to unfreeze."
                : "Time UNFROZEN. Time will now pass normally.",
            Data = new Dictionary<string, object>
            {
                ["frozen"] = _timeFreezeEnabled,
                ["currentTime"] = Game1.timeOfDay
            }
        };
    }

    private CommandResponse ExecuteCheatInfiniteEnergy(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        _infiniteEnergyEnabled = !_infiniteEnergyEnabled;

        if (_infiniteEnergyEnabled)
        {
            Game1.player.Stamina = Game1.player.MaxStamina;
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = _infiniteEnergyEnabled 
                ? "Infinite energy ENABLED. Stamina will stay at max."
                : "Infinite energy DISABLED. Stamina will drain normally.",
            Data = new Dictionary<string, object>
            {
                ["infiniteEnergy"] = _infiniteEnergyEnabled,
                ["currentStamina"] = Game1.player.Stamina,
                ["maxStamina"] = Game1.player.MaxStamina
            }
        };
    }

    private CommandResponse ExecuteCheatUnlockRecipes(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var craftingAdded = 0;
        var cookingAdded = 0;

        // Unlock all crafting recipes
        foreach (var kvp in DataLoader.CraftingRecipes(Game1.content))
        {
            if (!Game1.player.craftingRecipes.ContainsKey(kvp.Key))
            {
                Game1.player.craftingRecipes.Add(kvp.Key, 0);
                craftingAdded++;
            }
        }

        // Unlock all cooking recipes  
        foreach (var kvp in DataLoader.CookingRecipes(Game1.content))
        {
            if (!Game1.player.cookingRecipes.ContainsKey(kvp.Key))
            {
                Game1.player.cookingRecipes.Add(kvp.Key, 0);
                cookingAdded++;
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Unlocked {craftingAdded} crafting recipes and {cookingAdded} cooking recipes",
            Data = new Dictionary<string, object>
            {
                ["craftingRecipesAdded"] = craftingAdded,
                ["cookingRecipesAdded"] = cookingAdded,
                ["totalCraftingRecipes"] = Game1.player.craftingRecipes.Count,
                ["totalCookingRecipes"] = Game1.player.cookingRecipes.Count
            }
        };
    }

    private CommandResponse ExecuteCheatPetAllAnimals(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var pettedCount = 0;
        var animalNames = new List<string>();

        // Find all farm animals
        var farm = Game1.getFarm();
        if (farm != null)
        {
            // Animals in buildings
            foreach (var building in farm.buildings)
            {
                if (building.indoors.Value is AnimalHouse animalHouse)
                {
                    foreach (var animal in animalHouse.animals.Values)
                    {
                        if (!animal.wasPet.Value)
                        {
                            animal.pet(Game1.player, false);
                            pettedCount++;
                            animalNames.Add(animal.displayName);
                        }
                    }
                }
            }

            // Animals roaming outside
            foreach (var animal in farm.animals.Values)
            {
                if (!animal.wasPet.Value)
                {
                    animal.pet(Game1.player, false);
                    pettedCount++;
                    animalNames.Add(animal.displayName);
                }
            }
        }

        // Also pet the player's pet (dog/cat)
        var pet = Game1.player.getPet();
        if (pet != null)
        {
            pet.friendshipTowardFarmer.Value = Math.Min(pet.friendshipTowardFarmer.Value + 12, 1000);
            pettedCount++;
            animalNames.Add(pet.displayName);
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Petted {pettedCount} animals: {string.Join(", ", animalNames.Take(10))}" + 
                     (animalNames.Count > 10 ? $" and {animalNames.Count - 10} more" : ""),
            Data = new Dictionary<string, object>
            {
                ["pettedCount"] = pettedCount,
                ["animals"] = animalNames
            }
        };
    }

    private CommandResponse ExecuteCheatCompleteQuest(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var questLog = Game1.player.questLog;
        
        if (questLog.Count == 0)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No active quests to complete"
            };
        }

        // Complete specific quest by ID or name, or all quests
        var completedQuests = new List<string>();
        
        if (command.Params.TryGetValue("questId", out var questIdObj))
        {
            var questId = GetStringParam(questIdObj);
            var quest = questLog.FirstOrDefault(q => q.id.Value == questId || 
                                                      q.GetName().ToLower().Contains(questId.ToLower()));
            if (quest != null)
            {
                quest.questComplete();
                completedQuests.Add(quest.GetName());
            }
            else
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Quest not found: {questId}"
                };
            }
        }
        else
        {
            // Complete all quests
            foreach (var quest in questLog.ToList())
            {
                quest.questComplete();
                completedQuests.Add(quest.GetName());
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Completed {completedQuests.Count} quest(s): {string.Join(", ", completedQuests)}",
            Data = new Dictionary<string, object>
            {
                ["completedCount"] = completedQuests.Count,
                ["quests"] = completedQuests
            }
        };
    }

    private CommandResponse ExecuteCheatGiveGift(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("npcName", out var npcNameObj) ||
            !command.Params.TryGetValue("itemId", out var itemIdObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameters: npcName and itemId"
            };
        }

        var npcName = GetStringParam(npcNameObj);
        var itemId = GetStringParam(itemIdObj);

        // Find the NPC
        NPC? targetNpc = null;
        foreach (var location in Game1.locations)
        {
            targetNpc = location.getCharacterFromName(npcName);
            if (targetNpc != null) break;
            
            // Also check buildings
            foreach (var building in location.buildings)
            {
                if (building.indoors.Value != null)
                {
                    targetNpc = building.indoors.Value.getCharacterFromName(npcName);
                    if (targetNpc != null) break;
                }
            }
            if (targetNpc != null) break;
        }

        if (targetNpc == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"NPC not found: {npcName}"
            };
        }

        // Create the item
        var item = ItemRegistry.Create(itemId);
        if (item == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Invalid item ID: {itemId}"
            };
        }

        // Get friendship data
        var actualName = targetNpc.Name;
        if (!Game1.player.friendshipData.ContainsKey(actualName))
        {
            Game1.player.friendshipData.Add(actualName, new Friendship());
        }

        // Calculate gift taste and friendship gain
        var giftTaste = targetNpc.getGiftTasteForThisItem(item);
        var friendshipGain = giftTaste switch
        {
            0 => 80,   // Love
            2 => 45,   // Like
            4 => 20,   // Neutral
            6 => -20,  // Dislike
            8 => -40,  // Hate
            _ => 20
        };

        var tasteName = giftTaste switch
        {
            0 => "LOVE",
            2 => "Like",
            4 => "Neutral",
            6 => "Dislike", 
            8 => "HATE",
            _ => "Unknown"
        };

        var oldPoints = Game1.player.friendshipData[actualName].Points;
        Game1.player.friendshipData[actualName].Points += friendshipGain;
        Game1.player.friendshipData[actualName].GiftsToday++;
        Game1.player.friendshipData[actualName].GiftsThisWeek++;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Gave {item.DisplayName} to {actualName}. Reaction: {tasteName} (+{friendshipGain} points)",
            Data = new Dictionary<string, object>
            {
                ["npcName"] = actualName,
                ["item"] = item.DisplayName,
                ["taste"] = tasteName,
                ["friendshipGain"] = friendshipGain,
                ["oldPoints"] = oldPoints,
                ["newPoints"] = Game1.player.friendshipData[actualName].Points
            }
        };
    }

    #region New Farming Cheat Commands

    private CommandResponse ExecuteCheatHoeAll(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var hoedCount = 0;
        var skippedCount = 0;

        // Get optional radius parameter (default is entire visible area)
        int radius = 50;
        if (command.Params.TryGetValue("radius", out var radiusObj))
        {
            radius = GetIntParam(radiusObj, 50);
        }

        var playerTile = Game1.player.Tile;
        var minX = Math.Max(0, (int)playerTile.X - radius);
        var maxX = (int)playerTile.X + radius;
        var minY = Math.Max(0, (int)playerTile.Y - radius);
        var maxY = (int)playerTile.Y + radius;

        int existingDirtCount = 0;
        int objectBlockedCount = 0;
        int notDiggableCount = 0;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                var tile = new Vector2(x, y);

                // Skip if there's an object blocking
                if (location.Objects.ContainsKey(tile))
                {
                    objectBlockedCount++;
                    continue;
                }

                // Check if already has HoeDirt
                if (location.terrainFeatures.TryGetValue(tile, out var feature))
                {
                    if (feature is HoeDirt)
                    {
                        existingDirtCount++;
                        continue;
                    }
                    // Skip other terrain features (trees, grass, etc)
                    skippedCount++;
                    continue;
                }

                // Check if tile is tillable (has Diggable property OR is on Farm and looks like farmable ground)
                bool isTillable = location.doesTileHaveProperty(x, y, "Diggable", "Back") != null;
                
                // On Farm, also check if the tile is passable ground (more permissive)
                if (!isTillable && location is Farm)
                {
                    // Check if it's a basic passable tile without special properties
                    isTillable = location.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport) &&
                                 !location.isWaterTile(x, y) &&
                                 location.doesTileHaveProperty(x, y, "Water", "Back") == null &&
                                 location.doesTileHaveProperty(x, y, "Passable", "Buildings") == null;
                }

                if (isTillable)
                {
                    location.terrainFeatures.Add(tile, new HoeDirt(0, location));
                    hoedCount++;
                }
                else
                {
                    notDiggableCount++;
                }
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Hoed {hoedCount} tiles in {location.Name} (existing dirt: {existingDirtCount}, blocked: {objectBlockedCount}, terrain: {skippedCount}, not tillable: {notDiggableCount})",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["hoedCount"] = hoedCount,
                ["existingDirt"] = existingDirtCount,
                ["objectBlocked"] = objectBlockedCount,
                ["terrainBlocked"] = skippedCount,
                ["notTillable"] = notDiggableCount,
                ["radius"] = radius
            }
        };
    }

    private CommandResponse ExecuteCheatCutTrees(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var cutCount = 0;
        var collectedWood = new Dictionary<string, int>();

        // Get optional parameter to include stumps
        bool includeStumps = true;
        if (command.Params.TryGetValue("includeStumps", out var stumpsObj))
        {
            includeStumps = GetStringParam(stumpsObj, "true").ToLower() == "true";
        }

        var random = new Random();
        var featuresToRemove = new List<Vector2>();

        // Cut all trees
        foreach (var pair in location.terrainFeatures.Pairs.ToList())
        {
            if (pair.Value is Tree tree)
            {
                // Skip stumps if not requested
                if (!includeStumps && tree.stump.Value)
                    continue;

                featuresToRemove.Add(pair.Key);

                // Give wood drops based on tree growth stage
                int woodCount = tree.growthStage.Value >= 5 ? random.Next(10, 20) : random.Next(1, 5);
                var woodItem = ItemRegistry.Create("(O)388", woodCount); // Wood
                if (woodItem != null)
                {
                    Game1.player.addItemToInventory(woodItem);
                    if (!collectedWood.ContainsKey("Wood"))
                        collectedWood["Wood"] = 0;
                    collectedWood["Wood"] += woodCount;
                }

                // Chance for hardwood from mature trees
                if (tree.growthStage.Value >= 5 && random.NextDouble() < 0.2)
                {
                    var hardwood = ItemRegistry.Create("(O)709", random.Next(1, 3)); // Hardwood
                    if (hardwood != null)
                    {
                        Game1.player.addItemToInventory(hardwood);
                        if (!collectedWood.ContainsKey("Hardwood"))
                            collectedWood["Hardwood"] = 0;
                        collectedWood["Hardwood"] += hardwood.Stack;
                    }
                }

                // Chance for sap
                if (random.NextDouble() < 0.3)
                {
                    var sap = ItemRegistry.Create("(O)92", random.Next(1, 3)); // Sap
                    if (sap != null)
                    {
                        Game1.player.addItemToInventory(sap);
                        if (!collectedWood.ContainsKey("Sap"))
                            collectedWood["Sap"] = 0;
                        collectedWood["Sap"] += sap.Stack;
                    }
                }

                // Chance for tree seeds
                if (random.NextDouble() < 0.1)
                {
                    string seedId = tree.treeType.Value switch
                    {
                        "1" => "(O)309", // Acorn
                        "2" => "(O)310", // Maple Seed
                        "3" => "(O)311", // Pine Cone
                        _ => "(O)309"
                    };
                    var seed = ItemRegistry.Create(seedId, 1);
                    if (seed != null)
                    {
                        Game1.player.addItemToInventory(seed);
                        if (!collectedWood.ContainsKey(seed.DisplayName))
                            collectedWood[seed.DisplayName] = 0;
                        collectedWood[seed.DisplayName]++;
                    }
                }

                cutCount++;
            }
        }

        foreach (var pos in featuresToRemove)
        {
            location.terrainFeatures.Remove(pos);
        }

        // Also clear large stumps and logs from resource clumps
        var clumpsToRemove = new List<ResourceClump>();
        foreach (var clump in location.resourceClumps.ToList())
        {
            // 600 = Large Stump, 602 = Hollow Log
            if (clump.parentSheetIndex.Value == 600 || clump.parentSheetIndex.Value == 602)
            {
                int hardwoodCount = clump.parentSheetIndex.Value == 600 ? random.Next(2, 5) : random.Next(6, 10);
                var hardwood = ItemRegistry.Create("(O)709", hardwoodCount);
                if (hardwood != null)
                {
                    Game1.player.addItemToInventory(hardwood);
                    if (!collectedWood.ContainsKey("Hardwood"))
                        collectedWood["Hardwood"] = 0;
                    collectedWood["Hardwood"] += hardwoodCount;
                }
                clumpsToRemove.Add(clump);
                cutCount++;
            }
        }

        foreach (var clump in clumpsToRemove)
        {
            location.resourceClumps.Remove(clump);
        }

        var collectedSummary = string.Join(", ", collectedWood.Select(kv => $"{kv.Value}x {kv.Key}"));

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Cut {cutCount} trees/stumps in {location.Name}. Collected: {collectedSummary}",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["cutCount"] = cutCount,
                ["collected"] = collectedWood
            }
        };
    }

    private CommandResponse ExecuteCheatMineRocks(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var minedCount = 0;
        var collectedOres = new Dictionary<string, int>();
        var random = new Random();

        // Mine all stone/rock objects
        var objectsToRemove = new List<Vector2>();
        foreach (var pair in location.Objects.Pairs.ToList())
        {
            var obj = pair.Value;
            var name = obj.Name?.ToLower() ?? "";
            var qualifiedId = obj.QualifiedItemId ?? "";

            // Check if it's a stone/rock
            bool isStone = obj.Name == "Stone" ||
                          name.Contains("stone") ||
                          name.Contains("rock") ||
                          name.Contains("ore") ||
                          name.Contains("node") ||
                          qualifiedId == "(O)343" || // Stone
                          qualifiedId == "(O)450" || // Stone
                          qualifiedId == "(O)668" || // Stone
                          qualifiedId == "(O)670" || // Stone
                          qualifiedId == "(O)751" || // Copper Node
                          qualifiedId == "(O)290" || // Iron Node
                          qualifiedId == "(O)764" || // Gold Node
                          qualifiedId == "(O)765" || // Iridium Node
                          qualifiedId.StartsWith("(O)75") ||
                          qualifiedId.StartsWith("(O)76") ||
                          qualifiedId.StartsWith("(O)77");

            if (isStone)
            {
                objectsToRemove.Add(pair.Key);

                // Give stone drops
                int stoneCount = random.Next(1, 4);
                var stone = ItemRegistry.Create("(O)390", stoneCount);
                if (stone != null)
                {
                    Game1.player.addItemToInventory(stone);
                    if (!collectedOres.ContainsKey("Stone"))
                        collectedOres["Stone"] = 0;
                    collectedOres["Stone"] += stoneCount;
                }

                // Give ore based on node type
                if (qualifiedId == "(O)751" || name.Contains("copper"))
                {
                    var copper = ItemRegistry.Create("(O)378", random.Next(1, 4));
                    if (copper != null)
                    {
                        Game1.player.addItemToInventory(copper);
                        if (!collectedOres.ContainsKey("Copper Ore"))
                            collectedOres["Copper Ore"] = 0;
                        collectedOres["Copper Ore"] += copper.Stack;
                    }
                }
                else if (qualifiedId == "(O)290" || name.Contains("iron"))
                {
                    var iron = ItemRegistry.Create("(O)380", random.Next(1, 4));
                    if (iron != null)
                    {
                        Game1.player.addItemToInventory(iron);
                        if (!collectedOres.ContainsKey("Iron Ore"))
                            collectedOres["Iron Ore"] = 0;
                        collectedOres["Iron Ore"] += iron.Stack;
                    }
                }
                else if (qualifiedId == "(O)764" || name.Contains("gold"))
                {
                    var gold = ItemRegistry.Create("(O)384", random.Next(1, 4));
                    if (gold != null)
                    {
                        Game1.player.addItemToInventory(gold);
                        if (!collectedOres.ContainsKey("Gold Ore"))
                            collectedOres["Gold Ore"] = 0;
                        collectedOres["Gold Ore"] += gold.Stack;
                    }
                }
                else if (qualifiedId == "(O)765" || name.Contains("iridium"))
                {
                    var iridium = ItemRegistry.Create("(O)386", random.Next(1, 3));
                    if (iridium != null)
                    {
                        Game1.player.addItemToInventory(iridium);
                        if (!collectedOres.ContainsKey("Iridium Ore"))
                            collectedOres["Iridium Ore"] = 0;
                        collectedOres["Iridium Ore"] += iridium.Stack;
                    }
                }

                // Chance for coal
                if (random.NextDouble() < 0.05)
                {
                    var coal = ItemRegistry.Create("(O)382", 1);
                    if (coal != null)
                    {
                        Game1.player.addItemToInventory(coal);
                        if (!collectedOres.ContainsKey("Coal"))
                            collectedOres["Coal"] = 0;
                        collectedOres["Coal"]++;
                    }
                }

                // Chance for geode
                if (random.NextDouble() < 0.03)
                {
                    var geode = ItemRegistry.Create("(O)535", 1); // Geode
                    if (geode != null)
                    {
                        Game1.player.addItemToInventory(geode);
                        if (!collectedOres.ContainsKey("Geode"))
                            collectedOres["Geode"] = 0;
                        collectedOres["Geode"]++;
                    }
                }

                minedCount++;
            }
        }

        foreach (var pos in objectsToRemove)
        {
            location.Objects.Remove(pos);
        }

        // Also break large boulders from resource clumps
        var clumpsToRemove = new List<ResourceClump>();
        foreach (var clump in location.resourceClumps.ToList())
        {
            // 672 = Large boulder, 752/754/756/758 = Ore boulders
            if (clump.parentSheetIndex.Value == 672 ||
                clump.parentSheetIndex.Value == 752 ||
                clump.parentSheetIndex.Value == 754 ||
                clump.parentSheetIndex.Value == 756 ||
                clump.parentSheetIndex.Value == 758)
            {
                var clumpDrops = GetResourceClumpDrops(clump);
                foreach (var drop in clumpDrops)
                {
                    var item = ItemRegistry.Create(drop.itemId, drop.count);
                    if (item != null)
                    {
                        Game1.player.addItemToInventory(item);
                        if (!collectedOres.ContainsKey(item.DisplayName))
                            collectedOres[item.DisplayName] = 0;
                        collectedOres[item.DisplayName] += drop.count;
                    }
                }
                clumpsToRemove.Add(clump);
                minedCount++;
            }
        }

        foreach (var clump in clumpsToRemove)
        {
            location.resourceClumps.Remove(clump);
        }

        var collectedSummary = string.Join(", ", collectedOres.Select(kv => $"{kv.Value}x {kv.Key}"));

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Mined {minedCount} rocks/boulders in {location.Name}. Collected: {collectedSummary}",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["minedCount"] = minedCount,
                ["collected"] = collectedOres
            }
        };
    }

    private CommandResponse ExecuteCheatDigArtifacts(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var dugCount = 0;
        var collectedItems = new Dictionary<string, int>();
        var random = new Random();

        // Find and dig all artifact spots
        var objectsToRemove = new List<Vector2>();
        foreach (var pair in location.Objects.Pairs.ToList())
        {
            var obj = pair.Value;
            
            // Artifact spots have QualifiedItemId "(O)590"
            if (obj.QualifiedItemId == "(O)590" || obj.Name == "Artifact Spot")
            {
                objectsToRemove.Add(pair.Key);

                // Determine what to give based on location and season
                var drops = GetArtifactDrops(location, random);
                foreach (var drop in drops)
                {
                    var item = ItemRegistry.Create(drop.itemId, drop.count);
                    if (item != null)
                    {
                        Game1.player.addItemToInventory(item);
                        if (!collectedItems.ContainsKey(item.DisplayName))
                            collectedItems[item.DisplayName] = 0;
                        collectedItems[item.DisplayName] += drop.count;
                    }
                }

                dugCount++;
            }
        }

        foreach (var pos in objectsToRemove)
        {
            location.Objects.Remove(pos);
        }

        var collectedSummary = collectedItems.Count > 0 
            ? string.Join(", ", collectedItems.Select(kv => $"{kv.Value}x {kv.Key}"))
            : "nothing";

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Dug up {dugCount} artifact spots in {location.Name}. Found: {collectedSummary}",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["dugCount"] = dugCount,
                ["collected"] = collectedItems
            }
        };
    }

    private List<(string itemId, int count)> GetArtifactDrops(GameLocation location, Random random)
    {
        var drops = new List<(string itemId, int count)>();
        
        // Common artifacts and items from digging
        var commonArtifacts = new[]
        {
            "(O)96",  // Dwarf Scroll I
            "(O)97",  // Dwarf Scroll II
            "(O)98",  // Dwarf Scroll III
            "(O)99",  // Dwarf Scroll IV
            "(O)100", // Chipped Amphora
            "(O)101", // Arrowhead
            "(O)103", // Ancient Doll
            "(O)104", // Elvish Jewelry
            "(O)105", // Chewing Stick
            "(O)106", // Ornamental Fan
            "(O)108", // Ancient Drum
            "(O)109", // Golden Mask
            "(O)110", // Golden Relic
            "(O)111", // Strange Doll (green)
            "(O)112", // Strange Doll (yellow)
            "(O)113", // Chicken Statue
            "(O)114", // Ancient Seed
            "(O)115", // Prehistoric Tool
            "(O)116", // Dried Starfish
            "(O)117", // Anchor
            "(O)118", // Glass Shards
            "(O)119", // Bone Flute
            "(O)120", // Prehistoric Handaxe
            "(O)121", // Dwarvish Helm
            "(O)122", // Dwarf Gadget
            "(O)123", // Ancient Drum
            "(O)124", // Golden Mask
            "(O)125", // Golden Relic
            "(O)126", // Strange Doll
            "(O)127", // Strange Doll
        };

        var commonItems = new[]
        {
            "(O)330", // Clay
            "(O)390", // Stone
            "(O)382", // Coal
            "(O)535", // Geode
            "(O)536", // Frozen Geode
            "(O)537", // Magma Geode
            "(O)749", // Omni Geode
        };

        // 40% chance artifact, 60% chance common item
        if (random.NextDouble() < 0.4)
        {
            var artifact = commonArtifacts[random.Next(commonArtifacts.Length)];
            drops.Add((artifact, 1));
        }
        else
        {
            var item = commonItems[random.Next(commonItems.Length)];
            int count = item == "(O)330" ? random.Next(1, 4) : 1; // More clay
            drops.Add((item, count));
        }

        // Small chance for extra items
        if (random.NextDouble() < 0.2)
        {
            drops.Add(("(O)330", random.Next(1, 3))); // Clay
        }

        return drops;
    }

    private CommandResponse ExecuteCheatPlantSeeds(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        // Get seed ID parameter
        if (!command.Params.TryGetValue("seedId", out var seedIdObj))
        {
            // Return helpful season-specific seed suggestions
            var season = Game1.currentSeason;
            var suggestions = season switch
            {
                "spring" => "(O)472 Parsnip, (O)474 Cauliflower, (O)476 Potato, (O)427 Tulip",
                "summer" => "(O)479 Melon, (O)480 Tomato, (O)482 Pepper, (O)483 Wheat",
                "fall" => "(O)487 Corn, (O)488 Eggplant, (O)490 Pumpkin, (O)299 Amaranth",
                "winter" => "No outdoor crops in winter! Use greenhouse.",
                _ => "(O)472 Parsnip"
            };
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Missing seedId. Current season: {season}. Suggestions: {suggestions}"
            };
        }

        var seedId = GetStringParam(seedIdObj);
        var location = Game1.currentLocation;
        var plantedCount = 0;
        var skippedCount = 0;
        
        // Strip (O) prefix if present - Crop constructor may need just the ID
        string cropSeedId = seedId;
        if (seedId.StartsWith("(O)"))
        {
            cropSeedId = seedId.Substring(3);
        }

        // Find all empty HoeDirt tiles and plant seeds
        string failReason = "";
        foreach (var pair in location.terrainFeatures.Pairs.ToList())
        {
            if (pair.Value is HoeDirt dirt && dirt.crop == null)
            {
                try
                {
                    // Validate seed ID exists
                    var seedItem = ItemRegistry.Create(seedId);
                    if (seedItem == null)
                    {
                        if (string.IsNullOrEmpty(failReason)) failReason = $"Invalid seed ID: {seedId}";
                        skippedCount++;
                        continue;
                    }

                    // Create crop - the constructor validates season internally
                    // Try with the stripped ID first (numeric), fall back to full ID
                    var crop = new Crop(cropSeedId, (int)pair.Key.X, (int)pair.Key.Y, location);
                    
                    // Check if crop was created successfully (has growth phases)
                    if (crop.phaseDays == null || crop.phaseDays.Count == 0)
                    {
                        if (string.IsNullOrEmpty(failReason)) failReason = "Crop invalid for this season or location";
                        skippedCount++;
                        continue;
                    }

                    // Check if crop is dead (can happen with wrong season)
                    if (crop.dead.Value)
                    {
                        if (string.IsNullOrEmpty(failReason)) failReason = "Crop dies in current season";
                        skippedCount++;
                        continue;
                    }

                    dirt.crop = crop;
                    plantedCount++;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(failReason)) failReason = $"Exception: {ex.Message}";
                    skippedCount++;
                }
            }
        }

        var seedName = ItemRegistry.Create(seedId)?.DisplayName ?? seedId;
        var currentSeasonFinal = Game1.currentSeason;

        string resultMsg = $"Planted {plantedCount} {seedName} in {location.Name} (Season: {currentSeasonFinal})";
        if (skippedCount > 0)
        {
            resultMsg += $" ({skippedCount} failed";
            if (!string.IsNullOrEmpty(failReason)) resultMsg += $" - {failReason}";
            resultMsg += ")";
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = plantedCount > 0,
            Message = resultMsg,
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["plantedCount"] = plantedCount,
                ["skippedCount"] = skippedCount,
                ["seedId"] = seedId,
                ["seedName"] = seedName,
                ["currentSeason"] = currentSeasonFinal
            }
        };
    }

    private CommandResponse ExecuteCheatFertilizeAll(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        // Get optional fertilizer type (default to quality fertilizer)
        string fertilizerId = "(O)369"; // Quality Fertilizer
        if (command.Params.TryGetValue("fertilizerId", out var fertIdObj))
        {
            fertilizerId = GetStringParam(fertIdObj, "(O)369");
        }

        var location = Game1.currentLocation;
        var fertilizedCount = 0;

        // Find all HoeDirt tiles and apply fertilizer
        foreach (var pair in location.terrainFeatures.Pairs.ToList())
        {
            if (pair.Value is HoeDirt dirt)
            {
                // Check if already fertilized
                if (dirt.fertilizer.Value == null || dirt.fertilizer.Value == "0" || string.IsNullOrEmpty(dirt.fertilizer.Value))
                {
                    dirt.fertilizer.Value = fertilizerId;
                    fertilizedCount++;
                }
            }
        }

        var fertName = ItemRegistry.Create(fertilizerId)?.DisplayName ?? fertilizerId;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Applied {fertName} to {fertilizedCount} tiles in {location.Name}",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["fertilizedCount"] = fertilizedCount,
                ["fertilizerId"] = fertilizerId,
                ["fertilizerName"] = fertName
            }
        };
    }

    private CommandResponse ExecuteCheatSetSeason(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("season", out var seasonObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Missing season parameter. Current: {Game1.currentSeason}. Options: spring, summer, fall, winter"
            };
        }

        var season = GetStringParam(seasonObj).ToLower();
        if (season != "spring" && season != "summer" && season != "fall" && season != "winter")
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Invalid season '{season}'. Must be: spring, summer, fall, winter"
            };
        }

        var oldSeason = Game1.currentSeason;
        Game1.currentSeason = season;
        Game1.setGraphicsForSeason();

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Season changed from {oldSeason} to {season}",
            Data = new Dictionary<string, object>
            {
                ["oldSeason"] = oldSeason,
                ["newSeason"] = season
            }
        };
    }

    private CommandResponse ExecuteCheatUpgradeBackpack(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var player = Game1.player;
        var currentSize = player.MaxItems;
        
        // Backpack sizes: 12 (starter), 24 (large), 36 (deluxe)
        int newSize = 36; // Default to max
        if (command.Params.TryGetValue("size", out var sizeObj))
        {
            newSize = GetIntParam(sizeObj, 36);
            if (newSize != 12 && newSize != 24 && newSize != 36)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Invalid backpack size. Must be 12, 24, or 36. Current: {currentSize}"
                };
            }
        }

        player.MaxItems = newSize;
        
        // Expand inventory if needed
        while (player.Items.Count < newSize)
        {
            player.Items.Add(null);
        }

        string sizeName = newSize switch
        {
            12 => "Starter Backpack",
            24 => "Large Backpack", 
            36 => "Deluxe Backpack",
            _ => $"{newSize}-slot Backpack"
        };

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Backpack upgraded to {sizeName} ({newSize} slots). Was: {currentSize} slots",
            Data = new Dictionary<string, object>
            {
                ["oldSize"] = currentSize,
                ["newSize"] = newSize,
                ["sizeName"] = sizeName
            }
        };
    }

    private CommandResponse ExecuteCheatUpgradeTool(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("tool", out var toolObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing tool parameter. Options: Hoe, Pickaxe, Axe, WateringCan, FishingRod, Trash Can"
            };
        }

        var toolName = GetStringParam(toolObj);
        int upgradeLevel = 4; // Iridium by default
        if (command.Params.TryGetValue("level", out var levelObj))
        {
            upgradeLevel = GetIntParam(levelObj, 4);
            if (upgradeLevel < 0 || upgradeLevel > 4)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = "Invalid level. 0=Basic, 1=Copper, 2=Steel, 3=Gold, 4=Iridium"
                };
            }
        }

        var player = Game1.player;
        Tool? tool = null;
        
        // Find the tool in player's inventory
        foreach (var item in player.Items)
        {
            if (item is Tool t && t.Name.Contains(toolName, StringComparison.OrdinalIgnoreCase))
            {
                tool = t;
                break;
            }
        }

        // Handle Trash Can separately (it's not in inventory)
        if (toolName.Equals("Trash", StringComparison.OrdinalIgnoreCase) || 
            toolName.Equals("Trash Can", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("TrashCan", StringComparison.OrdinalIgnoreCase))
        {
            player.trashCanLevel = upgradeLevel;
            string[] levelNames = { "Basic", "Copper", "Steel", "Gold", "Iridium" };
            return new CommandResponse
            {
                Id = command.Id,
                Success = true,
                Message = $"Trash Can upgraded to {levelNames[upgradeLevel]} level",
                Data = new Dictionary<string, object>
                {
                    ["tool"] = "Trash Can",
                    ["level"] = upgradeLevel,
                    ["levelName"] = levelNames[upgradeLevel]
                }
            };
        }

        if (tool == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Tool '{toolName}' not found in inventory. Check: Hoe, Pickaxe, Axe, WateringCan, FishingRod"
            };
        }

        var oldLevel = tool.UpgradeLevel;
        tool.UpgradeLevel = upgradeLevel;

        string[] levels = { "Basic", "Copper", "Steel", "Gold", "Iridium" };
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"{tool.Name} upgraded from {levels[oldLevel]} to {levels[upgradeLevel]}",
            Data = new Dictionary<string, object>
            {
                ["tool"] = tool.Name,
                ["oldLevel"] = oldLevel,
                ["newLevel"] = upgradeLevel,
                ["levelName"] = levels[upgradeLevel]
            }
        };
    }

    private CommandResponse ExecuteCheatUpgradeAllTools(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        int upgradeLevel = 4; // Iridium by default
        if (command.Params.TryGetValue("level", out var levelObj))
        {
            upgradeLevel = GetIntParam(levelObj, 4);
            if (upgradeLevel < 0 || upgradeLevel > 4)
            {
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = "Invalid level. 0=Basic, 1=Copper, 2=Steel, 3=Gold, 4=Iridium"
                };
            }
        }

        var player = Game1.player;
        var upgradedTools = new List<string>();
        string[] levels = { "Basic", "Copper", "Steel", "Gold", "Iridium" };

        // Upgrade all tools in inventory
        foreach (var item in player.Items)
        {
            if (item is Tool tool && tool.UpgradeLevel >= 0)
            {
                // Skip tools that can't be upgraded (like Scythe)
                if (tool is MeleeWeapon) continue;
                
                tool.UpgradeLevel = upgradeLevel;
                upgradedTools.Add(tool.Name);
            }
        }

        // Upgrade trash can
        player.trashCanLevel = upgradeLevel;
        upgradedTools.Add("Trash Can");

        return new CommandResponse
        {
            Id = command.Id,
            Success = upgradedTools.Count > 0,
            Message = $"Upgraded {upgradedTools.Count} tools to {levels[upgradeLevel]}: {string.Join(", ", upgradedTools)}",
            Data = new Dictionary<string, object>
            {
                ["level"] = upgradeLevel,
                ["levelName"] = levels[upgradeLevel],
                ["tools"] = upgradedTools
            }
        };
    }

    private CommandResponse ExecuteCheatUnlockAll(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var player = Game1.player;
        var unlocked = new List<string>();

        // Max backpack
        player.MaxItems = 36;
        while (player.Items.Count < 36) player.Items.Add(null);
        unlocked.Add("Deluxe Backpack (36 slots)");

        // Upgrade all tools to iridium
        foreach (var item in player.Items)
        {
            if (item is Tool tool && !(tool is MeleeWeapon) && tool.UpgradeLevel >= 0)
            {
                tool.UpgradeLevel = 4;
            }
        }
        player.trashCanLevel = 4;
        unlocked.Add("All Tools to Iridium");

        // Unlock all crafting recipes
        foreach (var recipe in CraftingRecipe.craftingRecipes.Keys)
        {
            if (!player.craftingRecipes.ContainsKey(recipe))
            {
                player.craftingRecipes.Add(recipe, 0);
            }
        }
        unlocked.Add("All Crafting Recipes");

        // Unlock all cooking recipes
        foreach (var recipe in CraftingRecipe.cookingRecipes.Keys)
        {
            if (!player.cookingRecipes.ContainsKey(recipe))
            {
                player.cookingRecipes.Add(recipe, 0);
            }
        }
        unlocked.Add("All Cooking Recipes");

        // Max all skills
        string[] skills = { "Farming", "Fishing", "Foraging", "Mining", "Combat" };
        for (int i = 0; i < 5; i++)
        {
            player.experiencePoints[i] = 15000; // Level 10 = 15000 XP
        }
        unlocked.Add("All Skills to Level 10");

        // Give player golden scythe if they don't have it
        bool hasGoldenScythe = false;
        foreach (var item in player.Items)
        {
            if (item is MeleeWeapon weapon && weapon.Name.Contains("Golden Scythe"))
            {
                hasGoldenScythe = true;
                break;
            }
        }
        if (!hasGoldenScythe)
        {
            var goldenScythe = ItemRegistry.Create("(W)53"); // Golden Scythe
            if (goldenScythe != null)
            {
                player.addItemToInventory(goldenScythe);
                unlocked.Add("Golden Scythe");
            }
        }

        // Unlock copper pan
        player.hasRustyKey = true;
        player.hasSkullKey = true;
        player.hasSpecialCharm = true;
        player.hasDarkTalisman = true;
        player.hasMagicInk = true;
        player.hasClubCard = true;
        player.canUnderstandDwarves = true;
        unlocked.Add("All Special Items (Rusty Key, Skull Key, Club Card, etc.)");

        // Return to valley achievement (horse)
        if (!player.mailReceived.Contains("ccMovieTheater"))
        {
            player.mailReceived.Add("ccMovieTheater");
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Unlocked everything! {string.Join(", ", unlocked)}",
            Data = new Dictionary<string, object>
            {
                ["unlocked"] = unlocked
            }
        };
    }

    #endregion

    #region Targeted/Selective Cheat Commands

    /// <summary>
    /// Hoe specific tiles by coordinate list. Allows drawing shapes like hearts on the ground.
    /// Example tiles: [[10,20], [11,20], [12,20]] or "10,20;11,20;12,20"
    /// </summary>
    private CommandResponse ExecuteCheatHoeTiles(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var hoedCount = 0;
        var failedCount = 0;
        var failedReasons = new Dictionary<string, int>();

        // Parse tiles from params - supports both array format and string format
        var tiles = ParseTileCoordinates(command);
        if (tiles == null || tiles.Count == 0)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing or invalid 'tiles' parameter. Use array format [[x,y], [x,y]] or string format \"x,y;x,y\" or provide 'x' and 'y' for single tile."
            };
        }

        foreach (var tile in tiles)
        {
            var result = TryHoeTile(location, tile);
            if (result == null)
            {
                hoedCount++;
            }
            else
            {
                failedCount++;
                if (!failedReasons.ContainsKey(result))
                    failedReasons[result] = 0;
                failedReasons[result]++;
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = hoedCount > 0,
            Message = $"Hoed {hoedCount}/{tiles.Count} tiles in {location.Name}" + 
                     (failedCount > 0 ? $" (failed: {string.Join(", ", failedReasons.Select(kv => $"{kv.Key}={kv.Value}"))})" : ""),
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["hoedCount"] = hoedCount,
                ["failedCount"] = failedCount,
                ["totalRequested"] = tiles.Count,
                ["failedReasons"] = failedReasons
            }
        };
    }

    /// <summary>
    /// Clear specific tiles (remove objects, debris, terrain features) by coordinate list.
    /// </summary>
    private CommandResponse ExecuteCheatClearTiles(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;
        var clearedObjects = 0;
        var clearedFeatures = 0;
        var clearedDirt = 0;

        // Parse tiles from params
        var tiles = ParseTileCoordinates(command);
        if (tiles == null || tiles.Count == 0)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing or invalid 'tiles' parameter. Use array format [[x,y], [x,y]] or string format \"x,y;x,y\" or provide 'x' and 'y' for single tile."
            };
        }

        // Check what to clear (default: all)
        var clearObjects = true;
        var clearFeatures = true;
        var clearDirt = true;

        if (command.Params.TryGetValue("clearObjects", out var objVal))
            clearObjects = GetStringParam(objVal, "true").ToLower() == "true";
        if (command.Params.TryGetValue("clearFeatures", out var featVal))
            clearFeatures = GetStringParam(featVal, "true").ToLower() == "true";
        if (command.Params.TryGetValue("clearDirt", out var dirtVal))
            clearDirt = GetStringParam(dirtVal, "true").ToLower() == "true";

        foreach (var tile in tiles)
        {
            // Clear objects (debris, stones, items, etc.)
            if (clearObjects && location.Objects.ContainsKey(tile))
            {
                location.Objects.Remove(tile);
                clearedObjects++;
            }

            // Clear terrain features (grass, trees, HoeDirt)
            if (location.terrainFeatures.TryGetValue(tile, out var feature))
            {
                if (clearDirt && feature is HoeDirt)
                {
                    location.terrainFeatures.Remove(tile);
                    clearedDirt++;
                }
                else if (clearFeatures && !(feature is HoeDirt))
                {
                    location.terrainFeatures.Remove(tile);
                    clearedFeatures++;
                }
            }
        }

        var totalCleared = clearedObjects + clearedFeatures + clearedDirt;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Cleared {totalCleared} items from {tiles.Count} tiles in {location.Name} (objects: {clearedObjects}, features: {clearedFeatures}, hoed dirt: {clearedDirt})",
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["clearedObjects"] = clearedObjects,
                ["clearedFeatures"] = clearedFeatures,
                ["clearedDirt"] = clearedDirt,
                ["totalCleared"] = totalCleared,
                ["tilesProcessed"] = tiles.Count
            }
        };
    }

    /// <summary>
    /// Hoe tiles in a pattern relative to player or a center point.
    /// Patterns: heart, circle, square, line, cross, star, diamond, smiley
    /// </summary>
    private CommandResponse ExecuteCheatTillPattern(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        if (!command.Params.TryGetValue("pattern", out var patternObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing required parameter: pattern. Valid patterns: heart, circle, square, line, cross, star, diamond, smiley, spiral, arrow"
            };
        }

        var pattern = GetStringParam(patternObj).ToLower();
        var location = Game1.currentLocation;

        // Get center point (default: player position)
        int centerX = (int)Game1.player.Tile.X;
        int centerY = (int)Game1.player.Tile.Y;
        if (command.Params.TryGetValue("x", out var xObj))
            centerX = GetIntParam(xObj);
        if (command.Params.TryGetValue("y", out var yObj))
            centerY = GetIntParam(yObj);

        // Get size/scale parameter
        int size = 5;
        if (command.Params.TryGetValue("size", out var sizeObj))
            size = GetIntParam(sizeObj, 5);
        size = Math.Clamp(size, 1, 50);

        // Get direction for directional patterns (default: up/north)
        var direction = "up";
        if (command.Params.TryGetValue("direction", out var dirObj))
            direction = GetStringParam(dirObj, "up").ToLower();

        // Check if we should clear the area first (default: true for visibility)
        bool clearArea = true;
        if (command.Params.TryGetValue("clearArea", out var clearObj))
            clearArea = GetStringParam(clearObj, "true").ToLower() != "false";

        // Generate pattern tiles
        var patternTiles = GeneratePatternTiles(pattern, centerX, centerY, size, direction);

        if (patternTiles == null || patternTiles.Count == 0)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Unknown pattern: {pattern}. Valid patterns: heart, circle, square, line, cross, star, diamond, smiley, spiral, arrow"
            };
        }

        var patternTileSet = new HashSet<(int, int)>(patternTiles.Select(t => ((int)t.X, (int)t.Y)));
        var clearedCount = 0;

        // Get clear radius - how much area around pattern to clear (default: size * 2 + 5 for good visibility)
        int clearRadius = size * 2 + 5;
        if (command.Params.TryGetValue("clearRadius", out var clearRadiusObj))
            clearRadius = GetIntParam(clearRadiusObj, clearRadius);

        // If clearArea is true, clear all hoed dirt in radius around center (except pattern tiles)
        if (clearArea)
        {
            int minX = centerX - clearRadius;
            int maxX = centerX + clearRadius;
            int minY = centerY - clearRadius;
            int maxY = centerY + clearRadius;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    var tile = new Vector2(x, y);
                    // Don't clear pattern tiles
                    if (patternTileSet.Contains((x, y)))
                        continue;

                    // Clear hoed dirt that's NOT part of the pattern
                    if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature is HoeDirt)
                    {
                        location.terrainFeatures.Remove(tile);
                        clearedCount++;
                    }
                }
            }
        }

        // Hoe all pattern tiles
        var hoedCount = 0;
        var alreadyHoedCount = 0;
        var failedCount = 0;

        foreach (var tile in patternTiles)
        {
            // Check if already hoed - that's fine, count as success
            if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature is HoeDirt)
            {
                alreadyHoedCount++;
                continue;
            }

            var result = TryHoeTile(location, tile);
            if (result == null)
                hoedCount++;
            else
                failedCount++;
        }

        var totalSuccess = hoedCount + alreadyHoedCount;

        return new CommandResponse
        {
            Id = command.Id,
            Success = totalSuccess > 0,
            Message = $"Drew {pattern} pattern: {totalSuccess}/{patternTiles.Count} tiles at ({centerX}, {centerY}) with size {size}" +
                     (clearArea ? $" (cleared {clearedCount} surrounding tiles)" : ""),
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["pattern"] = pattern,
                ["centerX"] = centerX,
                ["centerY"] = centerY,
                ["size"] = size,
                ["hoedCount"] = hoedCount,
                ["alreadyHoed"] = alreadyHoedCount,
                ["failedCount"] = failedCount,
                ["clearedSurrounding"] = clearedCount,
                ["totalTiles"] = patternTiles.Count
            }
        };
    }

    /// <summary>
    /// Hoe any custom pattern using ASCII art grid or relative offsets.
    /// This allows the AI to draw ANY shape it can imagine!
    /// 
    /// Supported formats:
    /// 1. ASCII grid: Multi-line string where '#' or 'X' marks tiles to hoe
    ///    Example: "..#..\n.###.\n#####\n.###.\n..#.." draws a diamond
    /// 
    /// 2. Relative offsets: List of [dx,dy] pairs relative to center
    ///    Example: [[0,0], [1,0], [-1,0], [0,1], [0,-1]] draws a cross
    /// </summary>
    private CommandResponse ExecuteCheatHoeCustomPattern(GameCommand command)
    {
        if (!_cheatModeEnabled) return CheatModeDisabledResponse(command);

        var location = Game1.currentLocation;

        // Get center point (default: player position)
        int centerX = (int)Game1.player.Tile.X;
        int centerY = (int)Game1.player.Tile.Y;
        if (command.Params.TryGetValue("x", out var xObj))
            centerX = GetIntParam(xObj);
        if (command.Params.TryGetValue("y", out var yObj))
            centerY = GetIntParam(yObj);

        var tiles = new List<Vector2>();

        // Format 1: ASCII grid pattern
        if (command.Params.TryGetValue("grid", out var gridObj))
        {
            var grid = GetStringParam(gridObj);
            if (!string.IsNullOrEmpty(grid))
            {
                tiles = ParseAsciiGridPattern(grid, centerX, centerY);
            }
        }
        // Format 2: Relative offsets array [[dx,dy], [dx,dy], ...]
        else if (command.Params.TryGetValue("offsets", out var offsetsObj))
        {
            tiles = ParseRelativeOffsets(offsetsObj, centerX, centerY);
        }
        // Format 3: Offsets as string "dx,dy;dx,dy;dx,dy"
        else if (command.Params.TryGetValue("offsetString", out var offsetStrObj))
        {
            var offsetStr = GetStringParam(offsetStrObj);
            if (!string.IsNullOrEmpty(offsetStr))
            {
                tiles = ParseOffsetString(offsetStr, centerX, centerY);
            }
        }

        if (tiles.Count == 0)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No valid pattern provided. Use 'grid' (ASCII art where # or X marks tiles), 'offsets' (array of [dx,dy] pairs), or 'offsetString' (format: 'dx,dy;dx,dy')"
            };
        }

        // Check if we should clear the area first (default: true for visibility)
        bool clearArea = true;
        if (command.Params.TryGetValue("clearArea", out var clearObj))
            clearArea = GetStringParam(clearObj, "true").ToLower() != "false";

        var patternTileSet = new HashSet<(int, int)>(tiles.Select(t => ((int)t.X, (int)t.Y)));
        var clearedCount = 0;

        // Calculate pattern size for default clear radius
        int patternWidth = tiles.Count > 0 ? (int)(tiles.Max(t => t.X) - tiles.Min(t => t.X)) : 5;
        int patternHeight = tiles.Count > 0 ? (int)(tiles.Max(t => t.Y) - tiles.Min(t => t.Y)) : 5;
        int defaultRadius = Math.Max(patternWidth, patternHeight) + 5;

        // Get clear radius - how much area around pattern to clear
        int clearRadius = defaultRadius;
        if (command.Params.TryGetValue("clearRadius", out var clearRadiusObj))
            clearRadius = GetIntParam(clearRadiusObj, defaultRadius);

        // If clearArea is true, clear all hoed dirt in radius around center (except pattern tiles)
        if (clearArea && tiles.Count > 0)
        {
            int minX = centerX - clearRadius;
            int maxX = centerX + clearRadius;
            int minY = centerY - clearRadius;
            int maxY = centerY + clearRadius;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    var tile = new Vector2(x, y);
                    // Don't clear pattern tiles
                    if (patternTileSet.Contains((x, y)))
                        continue;

                    // Clear hoed dirt that's NOT part of the pattern
                    if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature is HoeDirt)
                    {
                        location.terrainFeatures.Remove(tile);
                        clearedCount++;
                    }
                }
            }
        }

        // Hoe all pattern tiles
        var hoedCount = 0;
        var alreadyHoedCount = 0;
        var failedCount = 0;
        var failedReasons = new Dictionary<string, int>();

        foreach (var tile in tiles)
        {
            // Check if already hoed - that's fine, count as success
            if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature is HoeDirt)
            {
                alreadyHoedCount++;
                continue;
            }

            var result = TryHoeTile(location, tile);
            if (result == null)
            {
                hoedCount++;
            }
            else
            {
                failedCount++;
                if (!failedReasons.ContainsKey(result))
                    failedReasons[result] = 0;
                failedReasons[result]++;
            }
        }

        var totalSuccess = hoedCount + alreadyHoedCount;

        return new CommandResponse
        {
            Id = command.Id,
            Success = totalSuccess > 0,
            Message = $"Custom pattern: drew {totalSuccess}/{tiles.Count} tiles centered at ({centerX}, {centerY})" +
                     (clearArea ? $" (cleared {clearedCount} surrounding tiles)" : "") +
                     (failedCount > 0 ? $" (failed: {string.Join(", ", failedReasons.Select(kv => $"{kv.Key}={kv.Value}"))})" : ""),
            Data = new Dictionary<string, object>
            {
                ["location"] = location.Name,
                ["centerX"] = centerX,
                ["centerY"] = centerY,
                ["hoedCount"] = hoedCount,
                ["alreadyHoed"] = alreadyHoedCount,
                ["failedCount"] = failedCount,
                ["clearedSurrounding"] = clearedCount,
                ["totalTiles"] = tiles.Count,
                ["failedReasons"] = failedReasons
            }
        };
    }

    /// <summary>Parse ASCII art grid into tile coordinates</summary>
    private List<Vector2> ParseAsciiGridPattern(string grid, int centerX, int centerY)
    {
        var tiles = new List<Vector2>();
        
        // Split into lines, handle both \n and actual newlines
        var lines = grid.Replace("\\n", "\n").Split('\n', StringSplitOptions.None);
        
        // Find dimensions to center the pattern
        int height = lines.Length;
        int width = lines.Max(l => l.Length);
        int offsetX = width / 2;
        int offsetY = height / 2;

        for (int row = 0; row < lines.Length; row++)
        {
            var line = lines[row];
            for (int col = 0; col < line.Length; col++)
            {
                char c = line[col];
                // '#', 'X', 'x', '*', '1' mark tiles to hoe
                if (c == '#' || c == 'X' || c == 'x' || c == '*' || c == '1' || c == '@')
                {
                    int tileX = centerX + (col - offsetX);
                    int tileY = centerY + (row - offsetY);
                    tiles.Add(new Vector2(tileX, tileY));
                }
            }
        }

        return tiles;
    }

    /// <summary>Parse relative offsets from JSON array</summary>
    private List<Vector2> ParseRelativeOffsets(object offsetsObj, int centerX, int centerY)
    {
        var tiles = new List<Vector2>();

        if (offsetsObj is JsonElement elem && elem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in elem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Array)
                {
                    var coords = item.EnumerateArray().ToList();
                    if (coords.Count >= 2)
                    {
                        int dx = coords[0].GetInt32();
                        int dy = coords[1].GetInt32();
                        tiles.Add(new Vector2(centerX + dx, centerY + dy));
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    int dx = item.TryGetProperty("dx", out var dxProp) ? dxProp.GetInt32() : 0;
                    int dy = item.TryGetProperty("dy", out var dyProp) ? dyProp.GetInt32() : 0;
                    tiles.Add(new Vector2(centerX + dx, centerY + dy));
                }
            }
        }

        return tiles;
    }

    /// <summary>Parse offset string format "dx,dy;dx,dy;dx,dy"</summary>
    private List<Vector2> ParseOffsetString(string offsetStr, int centerX, int centerY)
    {
        var tiles = new List<Vector2>();
        
        var pairs = offsetStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var coords = pair.Split(',');
            if (coords.Length >= 2 && 
                int.TryParse(coords[0].Trim(), out var dx) && 
                int.TryParse(coords[1].Trim(), out var dy))
            {
                tiles.Add(new Vector2(centerX + dx, centerY + dy));
            }
        }

        return tiles;
    }

    /// <summary>Parse tile coordinates from command params in various formats</summary>
    private List<Vector2>? ParseTileCoordinates(GameCommand command)
    {
        var tiles = new List<Vector2>();

        // Format 1: Single tile via x,y params
        if (command.Params.TryGetValue("x", out var xObj) && command.Params.TryGetValue("y", out var yObj))
        {
            // Check if it's arrays for multiple coordinates (JsonElement array)
            if (xObj is JsonElement xElem && xElem.ValueKind == JsonValueKind.Array &&
                yObj is JsonElement yElem && yElem.ValueKind == JsonValueKind.Array)
            {
                var xArr = xElem.EnumerateArray().ToList();
                var yArr = yElem.EnumerateArray().ToList();
                for (int i = 0; i < Math.Min(xArr.Count, yArr.Count); i++)
                {
                    tiles.Add(new Vector2(xArr[i].GetInt32(), yArr[i].GetInt32()));
                }
                return tiles;
            }
            
            tiles.Add(new Vector2(GetIntParam(xObj), GetIntParam(yObj)));
            return tiles;
        }

        // Format 2: Array of coordinate pairs [[x,y], [x,y], ...]
        if (command.Params.TryGetValue("tiles", out var tilesObj))
        {
            // Handle JsonElement array
            if (tilesObj is JsonElement tilesElem && tilesElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tilesElem.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array)
                    {
                        var coordArr = item.EnumerateArray().ToList();
                        if (coordArr.Count >= 2)
                        {
                            tiles.Add(new Vector2(coordArr[0].GetInt32(), coordArr[1].GetInt32()));
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        var x = item.TryGetProperty("x", out var xProp) ? xProp.GetInt32() : 0;
                        var y = item.TryGetProperty("y", out var yProp) ? yProp.GetInt32() : 0;
                        tiles.Add(new Vector2(x, y));
                    }
                }
                return tiles;
            }

            // Format 3: String format "x,y;x,y;x,y"
            var tilesStr = GetStringParam(tilesObj);
            if (!string.IsNullOrEmpty(tilesStr))
            {
                var pairs = tilesStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in pairs)
                {
                    var coords = pair.Split(',');
                    if (coords.Length >= 2 && int.TryParse(coords[0].Trim(), out var x) && int.TryParse(coords[1].Trim(), out var y))
                    {
                        tiles.Add(new Vector2(x, y));
                    }
                }
                return tiles;
            }
        }

        return tiles.Count > 0 ? tiles : null;
    }

    /// <summary>Try to hoe a single tile, returns null on success or error reason string</summary>
    private string? TryHoeTile(GameLocation location, Vector2 tile)
    {
        int x = (int)tile.X;
        int y = (int)tile.Y;

        // Skip if there's an object blocking
        if (location.Objects.ContainsKey(tile))
            return "object_blocking";

        // Check if already has HoeDirt
        if (location.terrainFeatures.TryGetValue(tile, out var feature))
        {
            if (feature is HoeDirt)
                return "already_hoed";
            return "terrain_blocking";
        }

        // Check if tile is tillable
        bool isTillable = location.doesTileHaveProperty(x, y, "Diggable", "Back") != null;

        // On Farm, be more permissive
        if (!isTillable && location is Farm)
        {
            isTillable = location.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport) &&
                         !location.isWaterTile(x, y) &&
                         location.doesTileHaveProperty(x, y, "Water", "Back") == null &&
                         location.doesTileHaveProperty(x, y, "Passable", "Buildings") == null;
        }

        if (!isTillable)
            return "not_tillable";

        location.terrainFeatures.Add(tile, new HoeDirt(0, location));
        return null; // Success
    }

    /// <summary>Generate tile coordinates for various patterns</summary>
    private List<Vector2> GeneratePatternTiles(string pattern, int centerX, int centerY, int size, string direction)
    {
        var tiles = new List<Vector2>();

        switch (pattern)
        {
            case "heart":
                tiles = GenerateHeartPattern(centerX, centerY, size);
                break;

            case "circle":
                tiles = GenerateCirclePattern(centerX, centerY, size);
                break;

            case "square":
                tiles = GenerateSquarePattern(centerX, centerY, size);
                break;

            case "filled_square":
                tiles = GenerateFilledSquarePattern(centerX, centerY, size);
                break;

            case "line":
                tiles = GenerateLinePattern(centerX, centerY, size, direction);
                break;

            case "cross":
                tiles = GenerateCrossPattern(centerX, centerY, size);
                break;

            case "star":
                tiles = GenerateStarPattern(centerX, centerY, size);
                break;

            case "diamond":
                tiles = GenerateDiamondPattern(centerX, centerY, size);
                break;

            case "smiley":
                tiles = GenerateSmileyPattern(centerX, centerY, size);
                break;

            case "spiral":
                tiles = GenerateSpiralPattern(centerX, centerY, size);
                break;

            case "arrow":
                tiles = GenerateArrowPattern(centerX, centerY, size, direction);
                break;
        }

        return tiles;
    }

    private List<Vector2> GenerateHeartPattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();
        
        // Pixel-art heart templates at different sizes
        // These are hand-crafted to look good as hoed tiles
        // Format: # = tile, . = empty, centered at (0,0)
        
        string[] heartSmall = {
            // 7x6 heart (size <= 4)
            ".##.##.",
            "#######",
            "#######",
            ".#####.",
            "..###..",
            "...#...",
        };
        
        string[] heartMedium = {
            // 9x8 heart (size 5-7)
            ".##...##.",
            "####.####",
            "#########",
            "#########",
            ".#######.",
            "..#####..",
            "...###...",
            "....#....",
        };
        
        string[] heartLarge = {
            // 11x10 heart (size 8-10)
            "..##...##..",
            ".####.####.",
            "###########",
            "###########",
            "###########",
            ".#########.",
            "..#######..",
            "...#####...",
            "....###....",
            ".....#.....",
        };
        
        string[] heartXLarge = {
            // 13x12 heart (size > 10)
            "..###...###..",
            ".#####.#####.",
            "#############",
            "#############",
            "#############",
            "#############",
            ".###########.",
            "..#########..",
            "...#######...",
            "....#####....",
            ".....###.....",
            "......#......",
        };
        
        // Select template based on size
        string[] template;
        if (size <= 4)
            template = heartSmall;
        else if (size <= 7)
            template = heartMedium;
        else if (size <= 10)
            template = heartLarge;
        else
            template = heartXLarge;
        
        int height = template.Length;
        int width = template[0].Length;
        
        // Calculate offset to center the pattern
        int offsetX = width / 2;
        int offsetY = height / 2;
        
        // Convert template to tile positions
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                if (template[row][col] == '#')
                {
                    int tileX = cx + (col - offsetX);
                    int tileY = cy + (row - offsetY);
                    tiles.Add(new Vector2(tileX, tileY));
                }
            }
        }

        return tiles;
    }

    private List<Vector2> GenerateCirclePattern(int cx, int cy, int radius)
    {
        var tiles = new List<Vector2>();
        var addedTiles = new HashSet<(int, int)>();

        // Bresenham circle algorithm
        for (double angle = 0; angle < Math.PI * 2; angle += 0.05)
        {
            int x = cx + (int)Math.Round(radius * Math.Cos(angle));
            int y = cy + (int)Math.Round(radius * Math.Sin(angle));
            
            if (!addedTiles.Contains((x, y)))
            {
                tiles.Add(new Vector2(x, y));
                addedTiles.Add((x, y));
            }
        }

        return tiles;
    }

    private List<Vector2> GenerateSquarePattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();

        // Outline only
        for (int i = -size; i <= size; i++)
        {
            tiles.Add(new Vector2(cx + i, cy - size)); // Top
            tiles.Add(new Vector2(cx + i, cy + size)); // Bottom
            tiles.Add(new Vector2(cx - size, cy + i)); // Left
            tiles.Add(new Vector2(cx + size, cy + i)); // Right
        }

        return tiles.Distinct().ToList();
    }

    private List<Vector2> GenerateFilledSquarePattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();

        for (int x = cx - size; x <= cx + size; x++)
        {
            for (int y = cy - size; y <= cy + size; y++)
            {
                tiles.Add(new Vector2(x, y));
            }
        }

        return tiles;
    }

    private List<Vector2> GenerateLinePattern(int cx, int cy, int length, string direction)
    {
        var tiles = new List<Vector2>();

        int dx = 0, dy = 0;
        switch (direction)
        {
            case "up": case "north": dy = -1; break;
            case "down": case "south": dy = 1; break;
            case "left": case "west": dx = -1; break;
            case "right": case "east": dx = 1; break;
            case "ne": case "northeast": dx = 1; dy = -1; break;
            case "nw": case "northwest": dx = -1; dy = -1; break;
            case "se": case "southeast": dx = 1; dy = 1; break;
            case "sw": case "southwest": dx = -1; dy = 1; break;
            default: dy = -1; break; // default up
        }

        for (int i = 0; i < length; i++)
        {
            tiles.Add(new Vector2(cx + dx * i, cy + dy * i));
        }

        return tiles;
    }

    private List<Vector2> GenerateCrossPattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();

        // Vertical line
        for (int i = -size; i <= size; i++)
        {
            tiles.Add(new Vector2(cx, cy + i));
        }

        // Horizontal line
        for (int i = -size; i <= size; i++)
        {
            tiles.Add(new Vector2(cx + i, cy));
        }

        return tiles.Distinct().ToList();
    }

    private List<Vector2> GenerateStarPattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();
        var addedTiles = new HashSet<(int, int)>();

        // 5-pointed star
        int points = 5;
        double outerRadius = size;
        double innerRadius = size * 0.4;

        for (int i = 0; i < points * 2; i++)
        {
            double angle1 = (i * Math.PI / points) - Math.PI / 2;
            double angle2 = ((i + 1) * Math.PI / points) - Math.PI / 2;
            double r1 = (i % 2 == 0) ? outerRadius : innerRadius;
            double r2 = ((i + 1) % 2 == 0) ? outerRadius : innerRadius;

            int x1 = cx + (int)Math.Round(r1 * Math.Cos(angle1));
            int y1 = cy + (int)Math.Round(r1 * Math.Sin(angle1));
            int x2 = cx + (int)Math.Round(r2 * Math.Cos(angle2));
            int y2 = cy + (int)Math.Round(r2 * Math.Sin(angle2));

            // Draw line between points using Bresenham
            foreach (var tile in GetLineTiles(x1, y1, x2, y2))
            {
                if (!addedTiles.Contains(((int)tile.X, (int)tile.Y)))
                {
                    tiles.Add(tile);
                    addedTiles.Add(((int)tile.X, (int)tile.Y));
                }
            }
        }

        return tiles;
    }

    private List<Vector2> GenerateDiamondPattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();

        // Diamond outline
        for (int i = 0; i <= size; i++)
        {
            tiles.Add(new Vector2(cx - size + i, cy - i)); // Top-left edge
            tiles.Add(new Vector2(cx + size - i, cy - i)); // Top-right edge
            tiles.Add(new Vector2(cx - size + i, cy + i)); // Bottom-left edge
            tiles.Add(new Vector2(cx + size - i, cy + i)); // Bottom-right edge
        }

        return tiles.Distinct().ToList();
    }

    private List<Vector2> GenerateSmileyPattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();
        var addedTiles = new HashSet<(int, int)>();

        // Face outline (circle)
        foreach (var tile in GenerateCirclePattern(cx, cy, size))
        {
            if (!addedTiles.Contains(((int)tile.X, (int)tile.Y)))
            {
                tiles.Add(tile);
                addedTiles.Add(((int)tile.X, (int)tile.Y));
            }
        }

        // Scale features based on size
        int eyeOffset = Math.Max(1, size / 3);
        int eyeY = cy - Math.Max(1, size / 3);
        int mouthY = cy + Math.Max(1, size / 4);
        int mouthWidth = Math.Max(1, size / 2);

        // Left eye
        tiles.Add(new Vector2(cx - eyeOffset, eyeY));
        addedTiles.Add((cx - eyeOffset, eyeY));

        // Right eye
        tiles.Add(new Vector2(cx + eyeOffset, eyeY));
        addedTiles.Add((cx + eyeOffset, eyeY));

        // Smile (arc)
        for (int i = -mouthWidth; i <= mouthWidth; i++)
        {
            int smileY = mouthY + Math.Abs(i) / 2; // Curved smile
            if (!addedTiles.Contains((cx + i, smileY)))
            {
                tiles.Add(new Vector2(cx + i, smileY));
                addedTiles.Add((cx + i, smileY));
            }
        }

        return tiles;
    }

    private List<Vector2> GenerateSpiralPattern(int cx, int cy, int size)
    {
        var tiles = new List<Vector2>();
        var addedTiles = new HashSet<(int, int)>();

        double maxAngle = Math.PI * 4; // 2 full rotations
        double angleStep = 0.1;

        for (double angle = 0; angle < maxAngle; angle += angleStep)
        {
            double radius = (angle / maxAngle) * size;
            int x = cx + (int)Math.Round(radius * Math.Cos(angle));
            int y = cy + (int)Math.Round(radius * Math.Sin(angle));

            if (!addedTiles.Contains((x, y)))
            {
                tiles.Add(new Vector2(x, y));
                addedTiles.Add((x, y));
            }
        }

        return tiles;
    }

    private List<Vector2> GenerateArrowPattern(int cx, int cy, int size, string direction)
    {
        var tiles = new List<Vector2>();

        // Arrow shaft
        var shaftTiles = GenerateLinePattern(cx, cy, size, direction);
        tiles.AddRange(shaftTiles);

        // Arrow head position (at the end of the shaft in the direction)
        int headX = cx, headY = cy;
        int perpX = 0, perpY = 0;

        switch (direction)
        {
            case "up": case "north":
                headY = cy - size + 1;
                perpX = 1; perpY = 1;
                break;
            case "down": case "south":
                headY = cy + size - 1;
                perpX = 1; perpY = -1;
                break;
            case "left": case "west":
                headX = cx - size + 1;
                perpX = 1; perpY = 1;
                break;
            case "right": case "east":
                headX = cx + size - 1;
                perpX = -1; perpY = 1;
                break;
            default:
                headY = cy - size + 1;
                perpX = 1; perpY = 1;
                break;
        }

        // Arrow head (V shape)
        int headSize = Math.Max(2, size / 3);
        for (int i = 1; i <= headSize; i++)
        {
            if (direction == "up" || direction == "north" || direction == "down" || direction == "south")
            {
                tiles.Add(new Vector2(headX - i, headY + i * (direction == "up" || direction == "north" ? 1 : -1)));
                tiles.Add(new Vector2(headX + i, headY + i * (direction == "up" || direction == "north" ? 1 : -1)));
            }
            else
            {
                tiles.Add(new Vector2(headX + i * (direction == "left" || direction == "west" ? 1 : -1), headY - i));
                tiles.Add(new Vector2(headX + i * (direction == "left" || direction == "west" ? 1 : -1), headY + i));
            }
        }

        return tiles.Distinct().ToList();
    }

    /// <summary>Get all tiles along a line using Bresenham's algorithm</summary>
    private List<Vector2> GetLineTiles(int x1, int y1, int x2, int y2)
    {
        var tiles = new List<Vector2>();

        int dx = Math.Abs(x2 - x1);
        int dy = Math.Abs(y2 - y1);
        int sx = x1 < x2 ? 1 : -1;
        int sy = y1 < y2 ? 1 : -1;
        int err = dx - dy;

        int x = x1, y = y1;

        while (true)
        {
            tiles.Add(new Vector2(x, y));

            if (x == x2 && y == y2) break;

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }

        return tiles;
    }

    #endregion

    #endregion
}

#region Command Classes

public class GameCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Action { get; set; } = "";
    public Dictionary<string, object> Params { get; set; } = new();
    public Action<CommandResponse>? OnComplete { get; set; }
}

public class CommandResponse
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<string, object>? Data { get; set; }
}

#endregion
