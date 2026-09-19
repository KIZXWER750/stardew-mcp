using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewMCP;

public partial class CommandExecutor
{
    private sealed class CropOption
    {
        public string SeedItemId { get; set; } = "";
        public string SeedName { get; set; } = "";
        public string HarvestItemId { get; set; } = "";
        public string HarvestName { get; set; } = "";
        public string Source { get; set; } = "";
        public int AvailableSeeds { get; set; }
        public int PaidSeeds { get; set; }
        public int Tiles { get; set; }
        public int SeedPrice { get; set; }
        public int UnitSellPrice { get; set; }
        public int GrowthDays { get; set; }
        public int RegrowDays { get; set; }
        public double ExpectedYield { get; set; }
        public CropProfitResult Projection { get; set; } = new();
    }

    private static string EconomicItemId(string value) => value.StartsWith("(") ? value : "(O)" + value;
    private static int MemberInt(object value, string name, int fallback = 0)
    {
        object? raw = ShopMember(value, name);
        return raw is int number ? number : fallback;
    }
    private static string MemberText(object value, string name)
        => ShopMember(value, name)?.ToString() ?? "";
    private static List<int> MemberInts(object value, string name)
        => ShopMember(value, name) is IEnumerable values
            ? values.Cast<object?>().Select(p => p is int n ? n : 0).ToList() : new();
    private static List<string> MemberTexts(object value, string name)
        => ShopMember(value, name) is IEnumerable values
            ? values.Cast<object?>().Select(p => p?.ToString() ?? "").ToList() : new();

    private IDictionary LoadGameDictionary(string assetName, string typeName)
    {
        Type valueType = ResolveGameDataType(typeName);
        MethodInfo load = Game1.content.GetType().GetMethods().First(p => p.Name == "Load" && p.IsGenericMethodDefinition
            && p.GetParameters().Length == 1 && p.GetParameters()[0].ParameterType == typeof(string));
        Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
        return load.MakeGenericMethod(dictionaryType).Invoke(Game1.content, new object[] { assetName }) as IDictionary
            ?? throw new InvalidOperationException($"Installed game data unavailable: {assetName}");
    }

    private static Type ResolveGameDataType(string typeName)
    {
        Type? resolved = AppDomain.CurrentDomain.GetAssemblies()
            .Select(p => p.GetType(typeName, false, false)).FirstOrDefault(p => p != null);
        if (resolved != null) return resolved;
        try { resolved = Assembly.Load(new AssemblyName("StardewValley.GameData")).GetType(typeName, false, false); }
        catch { }
        return resolved ?? throw new InvalidOperationException($"Installed game data type unavailable: {typeName}");
    }

    private Dictionary<string, int> PierreSeedPrices()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IDictionary shops = LoadGameDictionary("Data/Shops", "StardewValley.GameData.Shops.ShopData");
        if (!shops.Contains("SeedShop") || ShopMember(shops["SeedShop"]!, "Items") is not IEnumerable rows) return result;
        foreach (object row in rows)
        {
            string id = MemberText(row, "ItemId");
            int price = MemberInt(row, "Price", -1);
            string trade = MemberText(row, "TradeItemId");
            if (id != "" && price >= 0 && trade == "") result[EconomicItemId(id)] = price;
        }
        return result;
    }

    private List<CropOption> ReadCropOptions(int maxTiles, int reserveMoney)
    {
        int daysRemaining = 28 - Game1.dayOfMonth;
        int budget = Math.Max(0, Game1.player.Money - reserveMoney);
        var inventorySeeds = Game1.player.Items.Where(p => p is StardewValley.Object o && o.Category == StardewValley.Object.SeedsCategory)
            .GroupBy(p => p!.QualifiedItemId, StringComparer.OrdinalIgnoreCase).ToDictionary(p => p.Key, p => p.Sum(i => i!.Stack), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> prices;
        try { prices = PierreSeedPrices(); }
        catch (Exception ex) { _monitor.Log("[ECONOMY] Pierre catalog unavailable: " + ex.Message, StardewModdingAPI.LogLevel.Warn); prices = new(); }
        IDictionary crops = LoadGameDictionary("Data/Crops", "StardewValley.GameData.Crops.CropData");
        var result = new List<CropOption>();
        foreach (DictionaryEntry entry in crops)
        {
            string seedId = EconomicItemId(entry.Key?.ToString() ?? "");
            object data = entry.Value!;
            if (!MemberTexts(data, "Seasons").Any(p => p.Equals(Game1.currentSeason, StringComparison.OrdinalIgnoreCase))) continue;
            int growth = MemberInts(data, "DaysInPhase").Sum();
            if (growth < 1 || growth > daysRemaining) continue;
            string harvestId = EconomicItemId(MemberText(data, "HarvestItemId"));
            Item seed, harvest;
            try { seed = ItemRegistry.Create(seedId); harvest = ItemRegistry.Create(harvestId); }
            catch { continue; }
            if (seed is not StardewValley.Object seedObject || seedObject.Category != StardewValley.Object.SeedsCategory || harvest is not StardewValley.Object harvestObject) continue;
            int owned = inventorySeeds.TryGetValue(seedId, out int quantity) ? quantity : 0;
            int price = prices.TryGetValue(seedId, out int listed) ? listed : -1;
            int purchasable = price > 0 ? budget / price : 0;
            int tiles = Math.Min(maxTiles, owned + purchasable);
            if (tiles == 0) continue;
            int minYield = Math.Max(1, MemberInt(data, "HarvestMinStack", 1));
            int maxYield = Math.Max(minYield, MemberInt(data, "HarvestMaxStack", minYield));
            double extraChance = Math.Clamp(Convert.ToDouble(ShopMember(data, "ExtraHarvestChance") ?? 0d), 0, 0.99);
            double expectedYield = (minYield + maxYield) / 2d + extraChance / (1d - extraChance);
            int paidSeeds = Math.Max(0, tiles - owned);
            var projection = CropProfitMath.Calculate(new CropProfitInput { GrowthDays = growth,
                RegrowDays = MemberInt(data, "RegrowDays", -1), DaysRemaining = daysRemaining, Tiles = tiles,
                SeedPrice = paidSeeds == 0 ? 0 : price * paidSeeds / tiles, UnitSellPrice = harvestObject.sellToStorePrice(), ExpectedYieldPerHarvest = expectedYield });
            // Preserve the exact mixed owned/purchased cost instead of the rounded per-tile input above.
            projection.UpfrontCost = paidSeeds * Math.Max(0, price);
            projection.ExpectedProfit = projection.ExpectedRevenue - projection.UpfrontCost;
            result.Add(new CropOption { SeedItemId = seedId, SeedName = seed.DisplayName, HarvestItemId = harvestId,
                HarvestName = harvest.DisplayName, Source = owned >= tiles ? "inventory" : owned > 0 ? "inventory_and_pierre_catalog" : "pierre_catalog",
                AvailableSeeds = owned, PaidSeeds = paidSeeds, Tiles = tiles, SeedPrice = price, UnitSellPrice = harvestObject.sellToStorePrice(),
                GrowthDays = growth, RegrowDays = MemberInt(data, "RegrowDays", -1), ExpectedYield = expectedYield, Projection = projection });
        }
        return result.OrderByDescending(p => p.Projection.ExpectedProfit).ThenBy(p => p.GrowthDays).Take(24).ToList();
    }

    private CommandResponse CapabilityRegistry(GameCommand command)
    {
        string[] tools = { "Hoe", "Pickaxe", "Axe", "Watering Can", "Scythe" };
        var toolState = tools.Select(p => new { name = p, available = FarmToolSlot(p) >= 0 }).ToList();
        return FarmReply(command, new { status = "OBSERVED", version = "1.19.3", toolState,
            capabilities = new object[] {
                new {id="goal.money.persistence",supported=true,mode="verified_state"},
                new {id="economy.observe",supported=true,mode="read_only"},
                new {id="economy.crop_profit",supported=true,mode="read_only_installed_game_data"},
                new {id="economy.profit_opportunities",supported=true,mode="read_only_candidates"},
                new {id="goal.strategy_selection",supported=true,mode="persistent_plan"},
                new {id="goal.daily_plan",supported=true,mode="persistent_plan"},
                new {id="goal.plan_revalidation",supported=true,mode="state_fingerprint"},
                new {id="goal.plan_execution",supported=true,mode="leased_verified_steps"},
                new {id="goal.multi_day_resume",supported=true,mode="automatic_due_step_dispatch"},
                new {id="farm.restore_tilled_soil",supported=true,mode="verified_gameplay_input"},
                new {id="farm.crop_cycle",supported=true,mode="explicit_bounded_actions"},
                new {id="farm.existing_crop_cycle",supported=true,mode="water_harvest_and_sell_existing_only"},
                new {id="profit.shipping_bin",supported=false,mode="unavailable"},
                new {id="profit.fishing",supported=false,mode="unavailable"},
                new {id="profit.mining",supported=false,mode="unavailable"},
                new {id="goal.autonomous_strategy_execution",supported=true,mode="authorized_supported_crop_cycles"}
            }, note = "Support means a registered verified function exists; current inventory, tools, season, time, authorization and routes may still block a specific action." });
    }

    private object[] InventoryEconomicRows() => Game1.player.Items.Where(p => p is StardewValley.Object)
        .GroupBy(p => new { p!.QualifiedItemId, p.DisplayName, Quality = TradeQuality(p) })
        .Select(g => { var sample = (StardewValley.Object)g.First()!; int quantity = g.Sum(p => p!.Stack); int unit = sample.sellToStorePrice();
            return (object)new { itemId = g.Key.QualifiedItemId, name = g.Key.DisplayName, quality = g.Key.Quality,
                category = sample.Category, quantity, unitSellPrice = unit, totalSellValue = unit * quantity,
                isCrop = TradeCrop(sample), isSeed = sample.Category == StardewValley.Object.SeedsCategory }; }).ToArray();

    private CommandResponse EconomicState(GameCommand command)
    {
        RefreshLongTermGoalProgress();
        var farm = Game1.getFarm();
        var cropTiles = farm.terrainFeatures.Pairs.Where(p => p.Value is HoeDirt h && h.crop != null)
            .Select(p => { var h = (HoeDirt)p.Value; var crop = h.crop!; string harvestId=EconomicItemId(crop.indexOfHarvest.Value); int unitPrice=0;
                try { if(ItemRegistry.Create(harvestId) is StardewValley.Object harvest) unitPrice=harvest.sellToStorePrice(); } catch { }
                return new { x=(int)p.Key.X,y=(int)p.Key.Y,seedItemId=EconomicItemId(crop.netSeedIndex.Value),harvestItemId=harvestId,
                    daysUntilHarvest=ExistingCropDaysRemaining(crop),estimatedUnitSellPrice=unitPrice,
                    ready=!crop.dead.Value && crop.phaseDays.Count>0 && crop.currentPhase.Value>=crop.phaseDays.Count-1 && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value<=0),dead=crop.dead.Value,watered=h.state.Value==1 }; }).ToList();
        var inventory = InventoryEconomicRows();
        var goals = _goals.Goals.Where(p => p.Kind == GoalKinds.MoneyTarget && !GoalStatuses.IsTerminal(p.Status)).Select(p => new {p.Id,p.Summary,p.Status,p.Progress,p.Constraints}).ToList();
        return FarmReply(command, new { status="OBSERVED", money=Game1.player.Money, season=Game1.currentSeason,day=Game1.dayOfMonth,
            year=Game1.year,dayIndex=(int)Game1.stats.DaysPlayed,time=Game1.timeOfDay,daysRemainingInSeason=28-Game1.dayOfMonth,
            energy=Game1.player.Stamina,maxEnergy=Game1.player.MaxStamina,inventory,totalInventorySellValue=inventory.Sum(p => (int)(p.GetType().GetProperty("totalSellValue")!.GetValue(p) ?? 0)),
            farmCrops=new {count=cropTiles.Count,ready=cropTiles.Count(p=>p.ready),tiles=cropTiles},activeMoneyGoals=goals,
            limitations="Inventory values use live sellToStorePrice. Chest memory is intentionally excluded because it may be stale. Crop tiles are live Farm data; no route, labor, weather or authorization is implied." });
    }

    private CommandResponse CropProfitOptions(GameCommand command)
    {
        int requestedTiles = command.Params.ContainsKey("max_tiles") ? ShopInt(command,"max_tiles") : 0;
        int maxTiles = requestedTiles <= 0 ? 16 : Math.Clamp(requestedTiles,1,64);
        int reserve = command.Params.ContainsKey("reserve_money") ? Math.Max(0,ShopInt(command,"reserve_money")) : 0;
        var options = ReadCropOptions(maxTiles,reserve);
        return FarmReply(command,new {status="ANALYZED",season=Game1.currentSeason,day=Game1.dayOfMonth,daysRemaining=28-Game1.dayOfMonth,
            money=Game1.player.Money,reserveMoney=reserve,maxTiles,options,
            assumptions="Plant today; water every growth day; normal-quality live sell price; expected yield uses installed CropData stack range and extra-harvest chance. Pierre catalog prices come from installed Data/Shops and conditions/stock must be confirmed in the live shop before buying."});
    }

    private CommandResponse ProfitOpportunities(GameCommand command)
    {
        RefreshLongTermGoalProgress();
        int requestedTiles = command.Params.ContainsKey("max_tiles") ? ShopInt(command,"max_tiles") : 0;
        int maxTiles = requestedTiles <= 0 ? 16 : Math.Clamp(requestedTiles,1,64);
        int reserve = command.Params.ContainsKey("reserve_money") ? Math.Max(0,ShopInt(command,"reserve_money")) : 0;
        string goalId = ShopText(command,"goal_id");
        LongTermGoal? goal = goalId=="" ? _goals.Goals.FirstOrDefault(p=>p.Status==GoalStatuses.Active && p.Kind==GoalKinds.MoneyTarget)
            : _goals.Goals.FirstOrDefault(p=>p.Id.Equals(goalId,StringComparison.OrdinalIgnoreCase));
        int remaining = goal?.Progress.RemainingValue ?? 0;
        var inventory = InventoryEconomicRows();
        int cropSale = inventory.Where(p => (bool)(p.GetType().GetProperty("isCrop")!.GetValue(p) ?? false)).Sum(p => (int)(p.GetType().GetProperty("totalSellValue")!.GetValue(p) ?? 0));
        var cropOptions = ReadCropOptions(maxTiles,reserve).Take(8).ToList();
        var strategies = new List<object>();
        if(cropSale>0) strategies.Add(new {id="sell_inventory_crops",kind="immediate_sale",expectedGold=cropSale,upfrontCost=0,days=0,
            supported=true,confidence="high",coversRemaining=remaining>0&&cropSale>=remaining,requiredActions=new[]{"inspect_sellable_crops","travel/open Pierre","sell_crop_stack"}});
        strategies.AddRange(ReadExistingCropCandidates(maxTiles).Select(p=>(object)new {id=p.Id,kind=p.Kind,expectedGold=p.ExpectedGold,
            expectedProfit=p.ExpectedProfit,upfrontCost=0,days=p.Days,tiles=ReadMetaInt(p,"tiles"),supported=true,confidence=p.Confidence,
            coversRemaining=remaining>0&&p.ExpectedGold>=remaining,harvestItemId=p.Metadata["harvestItemId"],
            requiredActions=new[]{"water/harvest already-planted crops","sell_crop_stack"}}));
        strategies.AddRange(cropOptions.Select(p=>(object)new {id="plant_"+p.SeedItemId.Replace("(","").Replace(")",""),kind="crop_cycle",expectedGold=p.Projection.ExpectedRevenue,
            expectedProfit=p.Projection.ExpectedProfit,upfrontCost=p.Projection.UpfrontCost,days=p.GrowthDays,tiles=p.Tiles,supported=true,confidence="medium",
            coversRemaining=remaining>0&&p.Projection.ExpectedProfit>=remaining,seedItemId=p.SeedItemId,requiredActions=new[]{"confirm plot","obtain seeds if needed","till","plant","water daily","harvest","sell"}}));
        return FarmReply(command,new {status="ANALYZED",goal=goal==null?null:new {goal.Id,goal.Summary,remainingGold=remaining,goal.Constraints},strategies,
            excludedStrategies=new[]{new {id="shipping_bin",reason="no verified shipping-bin function"},new {id="fishing",reason="no verified fishing function"},new {id="mining",reason="no verified mining-profit loop"}},
            note="Candidates are comparisons only. This function does not select or execute a strategy; build_goal_plan may persist a selection, while every mutating action still requires goal authorization and live preflight."});
    }
}
