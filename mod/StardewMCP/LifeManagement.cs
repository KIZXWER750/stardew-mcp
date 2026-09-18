using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP;

public partial class CommandExecutor
{
    private string? _lifeEatItemId;
    private int _lifeEatSlot = -1;
    private int _lifeEatBeforeCount;
    private float _lifeEatBeforeEnergy;
    private int _lifeEatBeforeHealth;
    private DateTime _lifeEatStarted;
    private bool _lifeEatConfirmed;

    private static int LifeInt(GameCommand command, string key, int fallback = 0)
        => command.Params.TryGetValue(key, out var value) ? Convert.ToInt32(value is System.Text.Json.JsonElement json ? json.GetInt32() : value) : fallback;

    private static string LifeText(GameCommand command, string key)
        => command.Params.TryGetValue(key, out var value) ? (value is System.Text.Json.JsonElement json ? json.GetString() : value?.ToString()) ?? "" : "";

    private static int? LifeRecovery(StardewValley.Object item, string method)
    {
        try
        {
            var info = item.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var value = info?.Invoke(item, null);
            return value == null ? null : Convert.ToInt32(value);
        }
        catch { return null; }
    }

    private static int LifeInventoryCount(string qualifiedId)
        => Game1.player.Items.Where(item => item?.QualifiedItemId == qualifiedId).Sum(item => item!.Stack);

    private object LifeFoodRows()
        => Game1.player.Items.Select((item, slot) => new { item, slot })
            .Where(row => row.item is StardewValley.Object obj && obj.Edibility > 0)
            .Select(row =>
            {
                var obj = (StardewValley.Object)row.item!;
                return new
                {
                    slot = row.slot,
                    itemId = obj.QualifiedItemId,
                    name = obj.DisplayName,
                    stack = obj.Stack,
                    edibility = obj.Edibility,
                    energyRecovery = LifeRecovery(obj, "staminaRecoveredOnConsumption"),
                    healthRecovery = LifeRecovery(obj, "healthRecoveredOnConsumption"),
                    sellPrice = obj.sellToStorePrice(),
                    warning = obj.sellToStorePrice() > 0 ? "Consumable has sale value; preserve a reserve quantity." : ""
                };
            }).ToList();

    private CommandResponse LifeStatus(GameCommand command)
    {
        var player = Game1.player;
        int time = Game1.timeOfDay;
        int hour = time / 100, minute = time % 100;
        int minutesNow = hour * 60 + minute;
        int collapseMinutes = 26 * 60;
        int untilCollapse = Math.Max(0, collapseMinutes - minutesNow);
        double energyPercent = player.MaxStamina <= 0 ? 0 : Math.Round(player.Stamina / player.MaxStamina * 100.0, 1);
        string action = time >= 2400 ? "RETURN_HOME_NOW" : energyPercent <= 15 ? "RECOVER_OR_STOP_WORK" : time >= 2200 ? "PLAN_RETURN_HOME" : "CONTINUE_WITH_RESERVE";
        return FarmReply(command, new
        {
            status = "OBSERVED",
            location = Game1.currentLocation?.Name ?? "Unknown",
            x = (int)player.Tile.X,
            y = (int)player.Tile.Y,
            time,
            day = Game1.dayOfMonth,
            season = Game1.currentSeason,
            year = Game1.year,
            energy = player.Stamina,
            maxEnergy = player.MaxStamina,
            energyPercent,
            health = player.health,
            maxHealth = player.maxHealth,
            minutesUntil0200 = untilCollapse,
            recommendedAction = action,
            note = "Travel duration is not guessed. Call find_home_route before deciding the final departure time."
        });
    }

    private CommandResponse LifeFoodOptions(GameCommand command)
        => FarmReply(command, new
        {
            status = "OBSERVED",
            energy = Game1.player.Stamina,
            maxEnergy = Game1.player.MaxStamina,
            health = Game1.player.health,
            maxHealth = Game1.player.maxHealth,
            foods = LifeFoodRows(),
            rule = "Choose an exact observed item ID and slot. Keep a reserve and do not consume quest, requested, or user-protected items."
        });

    private CommandResponse LifeRecoveryOptions(GameCommand command)
    {
        string location = Game1.currentLocation?.Name ?? "Unknown";
        bool bathLoaded = Game1.getLocationFromName("BathHouse_Pool") != null;
        return FarmReply(command, new
        {
            status = "OBSERVED",
            energy = Game1.player.Stamina,
            maxEnergy = Game1.player.MaxStamina,
            health = Game1.player.health,
            maxHealth = Game1.player.maxHealth,
            location,
            food = LifeFoodRows(),
            sleep = new { availableAfterReturnHome = true, effect = "ends the day; sleep_until_morning verifies the next morning" },
            spa = new { mapKnown = bathLoaded, automatedRecoveryImplemented = false, reason = "Spa traversal and changing-room behavior require a separately verified gameplay function." }
        });
    }

    private CommandResponse LifeEnterFarmhouse(GameCommand command)
    {
        const int standX = 64, standY = 15, doorX = 64, doorY = 14;
        if (Game1.currentLocation?.Name == "FarmHouse")
            return FarmReply(command, new { status = "COMPLETED", location = "FarmHouse", alreadyInside = true });
        if (Game1.currentLocation?.Name != "Farm")
            return FarmReply(command, new { status = "BLOCKED", reason = "Reach Farm before entering the farmhouse.", location = Game1.currentLocation?.Name });
        if (Game1.activeClickableMenu != null || !Game1.player.CanMove || Game1.player.UsingTool)
            return FarmReply(command, new { status = "BLOCKED", reason = "Player or menu is busy at the farmhouse entrance." });
        int x = (int)Game1.player.Tile.X, y = (int)Game1.player.Tile.Y;
        if (x != standX || y != standY)
            return FarmReply(command, new { status = "BLOCKED", reason = "Stand immediately south of the farmhouse door first.", expected = new { x = standX, y = standY }, actual = new { x, y } });

        ClearMovementState();
        Game1.player.faceDirection(0);
        Game1.currentCursorTile = new Microsoft.Xna.Framework.Vector2(doorX, doorY);
        Game1.lastCursorMotionWasMouse = false;
        Game1.setMousePosition(doorX * 64 + 32 - Game1.viewport.X, doorY * 64 + 32 - Game1.viewport.Y);
        var action = Game1.options.actionButton.Length > 0 ? Game1.options.actionButton[0].ToSButton() : SButton.MouseRight;
        _monitor.Log($"[HOME ENTRANCE] stand=({standX},{standY}), facing=up, door=({doorX},{doorY})", LogLevel.Info);
        _helper.Input.Press(action);
        return FarmReply(command, new { status = "INPUT_SENT", stand = new { x = standX, y = standY }, door = new { x = doorX, y = doorY }, expectedLocation = "FarmHouse" });
    }

    private CommandResponse LifeEatStep(GameCommand command)
    {
        var player = Game1.player;
        bool reset = command.Params.ContainsKey("reset");
        if (reset)
        {
            if (Game1.activeClickableMenu != null) return FarmReply(command, new { status = "BLOCKED", reason = "Close the current menu before eating." });
            int slot = LifeInt(command, "slot", -1), reserve = Math.Max(0, LifeInt(command, "reserve_quantity", 0));
            string expected = LifeText(command, "item_id");
            if (slot < 0 || slot >= player.Items.Count || player.Items[slot] is not StardewValley.Object obj)
                return FarmReply(command, new { status = "BLOCKED", reason = "Observed food slot is no longer valid." });
            if (obj.QualifiedItemId != expected || obj.Edibility <= 0)
                return FarmReply(command, new { status = "BLOCKED", reason = "Item identity changed or item has no positive recovery." });
            if (LifeInventoryCount(expected) - 1 < reserve)
                return FarmReply(command, new { status = "BLOCKED", reason = "RESERVE_QUANTITY" });
            if (player.Stamina >= player.MaxStamina && player.health >= player.maxHealth)
                return FarmReply(command, new { status = "BLOCKED", reason = "No energy or health recovery is currently needed." });
            if (!player.CanMove || player.UsingTool)
                return FarmReply(command, new { status = "BLOCKED", reason = "Player is busy." });

            _lifeEatItemId = expected;
            _lifeEatSlot = slot;
            _lifeEatBeforeCount = LifeInventoryCount(expected);
            _lifeEatBeforeEnergy = player.Stamina;
            _lifeEatBeforeHealth = player.health;
            _lifeEatStarted = DateTime.UtcNow;
            _lifeEatConfirmed = false;
            player.CurrentToolIndex = slot;
            var action = Game1.options.actionButton.Length > 0 ? Game1.options.actionButton[0].ToSButton() : SButton.MouseRight;
            _helper.Input.Press(action);
            return FarmReply(command, new { status = "RUNNING", itemId = expected, slot, beforeCount = _lifeEatBeforeCount, message = "Normal eat interaction started." });
        }

        if (_lifeEatItemId == null || _lifeEatSlot < 0)
            return FarmReply(command, new { status = "BLOCKED", reason = "Eating task is not initialized." });

        int count = LifeInventoryCount(_lifeEatItemId);
        if (count == _lifeEatBeforeCount - 1)
        {
            var result = new
            {
                status = "COMPLETED",
                itemId = _lifeEatItemId,
                consumed = 1,
                countBefore = _lifeEatBeforeCount,
                countAfter = count,
                energyBefore = _lifeEatBeforeEnergy,
                energyAfter = player.Stamina,
                healthBefore = _lifeEatBeforeHealth,
                healthAfter = player.health
            };
            _lifeEatItemId = null; _lifeEatSlot = -1;
            return FarmReply(command, result);
        }
        if (count != _lifeEatBeforeCount)
        {
            _lifeEatItemId = null; _lifeEatSlot = -1;
            return FarmReply(command, new { status = "BLOCKED", reason = "Unexpected inventory delta; do not retry blindly." });
        }
        if ((DateTime.UtcNow - _lifeEatStarted).TotalSeconds > 12)
        {
            _lifeEatItemId = null; _lifeEatSlot = -1;
            return FarmReply(command, new { status = "BLOCKED", reason = "EAT_RESULT_TIMEOUT; item count did not change." });
        }
        if (Game1.activeClickableMenu is DialogueBox)
        {
            string question = Game1.currentLocation.lastQuestionKey ?? "";
            if (!question.Contains("Eat", StringComparison.OrdinalIgnoreCase))
            {
                _lifeEatItemId = null; _lifeEatSlot = -1;
                return FarmReply(command, new { status = "BLOCKED", reason = "Unrelated dialogue opened; no answer was selected.", question });
            }
            if (!_lifeEatConfirmed)
            {
                Game1.activeClickableMenu = null;
                Game1.currentLocation.answerDialogue(new Response("Yes", "Yes"));
                _lifeEatConfirmed = true;
                return FarmReply(command, new { status = "RUNNING", message = "Selected Yes on the verified eat confirmation." });
            }
        }
        return FarmReply(command, new { status = "RUNNING", message = "Waiting for verified inventory and recovery change." });
    }
}
