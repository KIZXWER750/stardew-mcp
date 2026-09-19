using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private string _craftObservation = "";
    private string _craftObservedRecipe = "";
    private readonly Dictionary<string, (string Fingerprint, object Result)> _craftReceipts = new(StringComparer.Ordinal);

    private static string CraftIngredientName(string rawId)
    {
        if (!int.TryParse(rawId, out int id))
        {
            try { return ItemRegistry.Create(rawId).DisplayName; }
            catch { return rawId; }
        }
        if (id < 0) return id switch
        {
            StardewValley.Object.VegetableCategory => "Any Vegetable",
            StardewValley.Object.FruitsCategory => "Any Fruit",
            StardewValley.Object.FishCategory => "Any Fish",
            StardewValley.Object.EggCategory => "Any Egg",
            StardewValley.Object.MilkCategory => "Any Milk",
            _ => $"Item category {id}"
        };
        try { return ItemRegistry.Create($"(O){id}").DisplayName; }
        catch { return $"Item {id}"; }
    }

    private static int CraftIngredientCount(string rawId)
    {
        if (int.TryParse(rawId, out int id))
            return Game1.player.Items.Where(item => item != null && (id < 0 ? item.Category == id : item.ParentSheetIndex == id)).Sum(item => item!.Stack);
        return Game1.player.Items.Where(item => item?.QualifiedItemId == rawId || item?.ItemId == rawId).Sum(item => item!.Stack);
    }

    private static string? ResolveKnownCraftingRecipe(string requested, out List<string> matches)
    {
        requested = requested.Trim();
        matches = new();
        foreach (string name in Game1.player.craftingRecipes.Keys)
        {
            var recipe = new CraftingRecipe(name);
            string outputName = recipe.createItem().DisplayName;
            if (name.Equals(requested, StringComparison.OrdinalIgnoreCase)
                || outputName.Equals(requested, StringComparison.OrdinalIgnoreCase)) return name;
            if (name.Contains(requested, StringComparison.OrdinalIgnoreCase)
                || outputName.Contains(requested, StringComparison.OrdinalIgnoreCase)) matches.Add(name);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static int CraftOutputCapacity(Item output)
    {
        int capacity = 0;
        foreach (Item? item in Game1.player.Items)
        {
            if (item == null) capacity += output.maximumStackSize();
            else if (item.canStackWith(output)) capacity += Math.Max(0, item.maximumStackSize() - item.Stack);
        }
        return capacity;
    }

    private CommandResponse InspectCraftingRecipe(GameCommand command)
    {
        string requested = ShopText(command, "recipe");
        if (requested == "") throw new InvalidOperationException("Recipe name is required.");
        string? name = ResolveKnownCraftingRecipe(requested, out List<string> matches);
        if (name == null)
            return FarmReply(command, new { status = matches.Count > 1 ? "AMBIGUOUS" : "NOT_KNOWN", requested, matches, movementPerformed = false });

        var recipe = new CraftingRecipe(name);
        Item output = recipe.createItem();
        int crafts = Math.Max(1, ShopInt(command, "quantity"));
        if (crafts > 99) throw new InvalidOperationException("Quantity must be 1..99.");
        var ingredients = recipe.recipeList.Select(pair =>
        {
            int available = CraftIngredientCount(pair.Key);
            int required = checked(pair.Value * crafts);
            return new { itemId = pair.Key, name = CraftIngredientName(pair.Key), required, available, missing = Math.Max(0, required - available) };
        }).ToList();
        int outputQuantity = checked(output.Stack * crafts);
        int capacity = CraftOutputCapacity(output);
        _craftObservation = Guid.NewGuid().ToString("N");
        _craftObservedRecipe = name;
        return FarmReply(command, new
        {
            status = "OBSERVED", observationId = _craftObservation, recipe = name, requestedQuantity = crafts,
            output = new { itemId = output.QualifiedItemId, name = output.DisplayName, quantity = outputQuantity },
            ingredients, ingredientsReady = ingredients.All(p => p.missing == 0), outputCapacity = capacity,
            canCraft = ingredients.All(p => p.missing == 0) && capacity >= outputQuantity,
            movementPerformed = false,
            note = "This live installed-game recipe and inventory observation outranks cached wiki facts. Re-inspect after acquiring or moving materials."
        });
    }

    private CommandResponse CraftObservedItem(GameCommand command)
    {
        string request = ShopText(command, "request_id");
        string observation = ShopText(command, "observation_id");
        string requested = ShopText(command, "recipe");
        int crafts = ShopInt(command, "quantity");
        if (request.Length < 8 || request.Length > 128 || observation == "" || requested == "" || crafts < 1 || crafts > 99)
            throw new InvalidOperationException("Valid request_id, observation_id, recipe and quantity 1..99 are required.");
        string fingerprint = $"{requested}|{crafts}";
        if (_craftReceipts.TryGetValue(request, out var prior))
        {
            if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("REQUEST_ID_CONFLICT");
            return FarmReply(command, prior.Result);
        }
        string? name = ResolveKnownCraftingRecipe(requested, out _);
        if (name == null || name != _craftObservedRecipe || observation != _craftObservation)
            throw new InvalidOperationException("CRAFT_OBSERVATION_STALE: inspect the exact recipe again.");

        var recipe = new CraftingRecipe(name);
        Item preview = recipe.createItem();
        var missing = recipe.recipeList.Select(pair => new
        {
            itemId = pair.Key, name = CraftIngredientName(pair.Key),
            required = checked(pair.Value * crafts), available = CraftIngredientCount(pair.Key)
        }).Where(p => p.available < p.required).ToList();
        int expectedOutput = checked(preview.Stack * crafts);
        if (missing.Count > 0 || CraftOutputCapacity(preview) < expectedOutput)
            return FarmReply(command, new { status = "BLOCKED", reason = missing.Count > 0 ? "MISSING_INGREDIENTS" : "INVENTORY_FULL", recipe = name, missing, expectedOutput, movementPerformed = false });

        int beforeOutput = Game1.player.Items.Where(p => p?.QualifiedItemId == preview.QualifiedItemId).Sum(p => p!.Stack);
        object reserved = new { status = "FAILED", reason = "CRAFT_OUTCOME_UNKNOWN", requestId = request, recipe = name };
        _craftReceipts[request] = (fingerprint, reserved);
        _craftObservation = "";
        try
        {
            for (int i = 0; i < crafts; i++)
            {
                if (!recipe.doesFarmerHaveIngredientsInInventory()) throw new InvalidOperationException("INGREDIENTS_CHANGED");
                recipe.consumeIngredients(null);
                Item made = recipe.createItem();
                Item? leftover = Game1.player.addItemToInventory(made);
                if (leftover != null)
                {
                    Game1.createItemDebris(leftover, Game1.player.Position, -1, Game1.currentLocation);
                    throw new InvalidOperationException("OUTPUT_CAPACITY_CHANGED; leftover dropped at player position");
                }
                Game1.player.craftingRecipes[name]++;
            }
            int received = Game1.player.Items.Where(p => p?.QualifiedItemId == preview.QualifiedItemId).Sum(p => p!.Stack) - beforeOutput;
            object result = new { status = received == expectedOutput ? "COMPLETED" : "BLOCKED", requestId = request, recipe = name,
                itemId = preview.QualifiedItemId, item = preview.DisplayName, crafts, expectedOutput, received,
                reason = received == expectedOutput ? "" : "OUTPUT_VERIFICATION_FAILED" };
            _craftReceipts[request] = (fingerprint, result);
            return FarmReply(command, result);
        }
        catch (Exception ex)
        {
            object result = new { status = "BLOCKED", requestId = request, recipe = name, reason = ex.Message,
                received = Game1.player.Items.Where(p => p?.QualifiedItemId == preview.QualifiedItemId).Sum(p => p!.Stack) - beforeOutput,
                requiresInspection = true };
            _craftReceipts[request] = (fingerprint, result);
            return FarmReply(command, result);
        }
    }
}
