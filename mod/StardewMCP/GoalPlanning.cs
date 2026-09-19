using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewMCP;

public partial class CommandExecutor
{
    private sealed class ExistingCropPlanTile
    {
        public int X { get; init; }
        public int Y { get; init; }
        public string HarvestId { get; init; } = "";
        public string HarvestName { get; init; } = "";
        public int UnitPrice { get; init; }
        public int Days { get; init; }
        public string HarvestMethod { get; init; } = "Grab";
    }

    private CommandResponse BuildGoalPlanCommand(GameCommand command, bool refresh)
    {
        RefreshLongTermGoalProgress();
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        if (goal.Status != GoalStatuses.Active) throw new InvalidOperationException("Only an active goal can receive an execution plan.");
        if (goal.Plan.Status is GoalPlanStatuses.Executing or GoalPlanStatuses.Waiting)
            throw new InvalidOperationException("The current plan is executing. Finish, pause or block its leased step before replanning.");
        int requestedTiles = command.Params.ContainsKey("max_tiles") ? ShopInt(command, "max_tiles") : 0;
        int maxTiles = requestedTiles <= 0
            ? refresh && goal.Plan.MaxTiles > 0 ? Math.Clamp(goal.Plan.MaxTiles, 1, 64) : 16
            : Math.Clamp(requestedTiles, 1, 64);
        string fingerprint = GoalPlanFingerprint(goal, maxTiles);
        if (!refresh && goal.Plan.Status == GoalPlanStatuses.Ready && goal.Plan.StateFingerprint == fingerprint
            && GoalPlanPolicy.HasSafeShape(goal.Plan))
            return FarmReply(command, new { status = "UNCHANGED", goalId = goal.Id, plan = goal.Plan, stale = false });

        List<GoalPlanCandidate> candidates = BuildPlanCandidates(goal, maxTiles);
        int dayIndex = CurrentDayIndex();
        int startOffset = Game1.timeOfDay >= goal.Constraints.LatestWorkTime ? 1 : 0;
        var deadlineEligible = candidates.Where(p => !goal.Money.DeadlineDayIndex.HasValue
            || dayIndex + startOffset + p.Days <= goal.Money.DeadlineDayIndex.Value).ToList();
        var authorizedCandidates = deadlineEligible.Where(p => p.RequiredActions.All(action => GoalActionAuthorized(goal, action))).ToList();
        var selectionPool = authorizedCandidates.Count > 0 ? authorizedCandidates : deadlineEligible;
        GoalPlanCandidate? selected = GoalPlanPolicy.Select(selectionPool, goal.Constraints.StrategyPreference, goal.Progress.RemainingValue);
        if (selected == null)
        {
            string reason = candidates.Count > 0 && deadlineEligible.Count == 0 ? "NO_CANDIDATE_MEETS_DEADLINE" : "NO_SUPPORTED_PROFIT_CANDIDATE";
            SaveBlockedGoalPlan(goal, fingerprint, reason, new(), maxTiles: maxTiles);
            bool newCropActionsAlreadyAuthorized = GoalActionAuthorized(goal, "buy_seeds") && GoalActionAuthorized(goal, "farm_crops");
            string question = reason == "NO_CANDIDATE_MEETS_DEADLINE"
                ? "현재 허용된 방법으로는 마감일까지 목표를 달성할 후보가 없습니다. 목표 조건을 어떻게 바꿀까요?"
                : newCropActionsAlreadyAuthorized
                ? $"씨앗 구매와 새 농사는 이미 허용되어 있지만 현재 {Game1.player.Money}g와 예비금·계절 조건으로 수익 후보를 만들지 못했습니다. 구매 가능한 소규모 재배로 다시 계산할까요, 아니면 다른 수익 방법을 검토할까요?"
                : "인벤토리에 판매할 수확물도, 농장에 관리할 수 있는 기존 작물도 없습니다. 직접 수확물을 준비할까요, 아니면 buy_seeds, farm_crops 행동 허용을 검토할까요?";
            string[] options = reason == "NO_CANDIDATE_MEETS_DEADLINE"
                ? new[] { "마감 조건을 다시 정하기", "목표 일시정지", "목표 취소" }
                : newCropActionsAlreadyAuthorized
                ? new[] { "구매 가능한 수량만 소규모 재배", "다른 수익 방법 허용 검토", "목표 일시정지", "목표 취소" }
                : new[] { "판매할 수확물을 준비한 뒤 재개", "buy_seeds, farm_crops 허용 검토", "목표 일시정지", "목표 취소" };
            return FarmReply(command, new { status = "USER_INPUT_REQUIRED", goalId = goal.Id, reason, plan = goal.Plan,
                requiresUserInput = true, suggestedQuestion = question, suggestedOptions = options,
                note = "Call request_goal_input with this question and options. Do not end with a chat-only block or invent a resource." });
        }

        List<string> missing = selected.RequiredActions.Where(p => !GoalActionAuthorized(goal, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            SaveBlockedGoalPlan(goal, fingerprint, "MISSING_ACTION_AUTHORIZATION", missing, selected, maxTiles);
            return FarmReply(command, new { status = "USER_INPUT_REQUIRED", goalId = goal.Id, reason = "MISSING_ACTION_AUTHORIZATION",
                missingAuthorizations = missing, selectedCandidate = selected, plan = goal.Plan, requiresUserInput = true,
                suggestedQuestion = $"이 목표를 위해 다음 행동을 허용할까요? {string.Join(", ", missing)}",
                suggestedOptions = new[] { "필요한 행동 모두 허용", "계획만 유지", "목표 일시정지" },
                note = "Call request_goal_input to show the dedicated in-game response window. Do not execute or infer permission." });
        }

        GoalExecutionPlan plan = CreateExecutionPlan(goal, selected, fingerprint, maxTiles, startOffset);
        goal.Plan = plan;
        TouchGoal(goal);
        _goalsDirty = true;
        FlushLongTermMemory();
        return FarmReply(command, new { status = "PLANNED", goalId = goal.Id, plan,
            consideredCandidates = RankForResponse(deadlineEligible, goal).Take(8).Select(p => new {
                p.Id, p.Kind, p.Title, p.ExpectedGold, p.ExpectedProfit, p.UpfrontCost, p.Days, p.WorkUnits, p.Confidence, p.RequiredActions
            }).ToList(),
            note = "The plan is persistent and ready for Phase 4 execution. Call start_goal_plan_execution; every mutating step still requires live preflight and verified completion." });
    }

    private CommandResponse InspectGoalPlanCommand(GameCommand command)
    {
        RefreshLongTermGoalProgress();
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        int requestedTiles = command.Params.ContainsKey("max_tiles") ? ShopInt(command, "max_tiles") : 0;
        int maxTiles = requestedTiles <= 0 ? Math.Clamp(goal.Plan.MaxTiles, 1, 64) : Math.Clamp(requestedTiles, 1, 64);
        bool stale = goal.Plan.Status == GoalPlanStatuses.Stale
            || goal.Plan.Status == GoalPlanStatuses.Ready && goal.Plan.StateFingerprint != GoalPlanFingerprint(goal, maxTiles);
        string effectiveStatus = stale ? GoalPlanStatuses.Stale : goal.Plan.Status;
        return FarmReply(command, new { status = "OBSERVED", goalId = goal.Id, plan = goal.Plan, effectiveStatus, stale,
            reason = stale ? "Money, day, season, relevant inventory or goal constraints changed after planning." : "",
            note = stale ? "Call refresh_goal_plan before relying on this plan." : "Inspection does not execute any step." });
    }

    private List<GoalPlanCandidate> BuildPlanCandidates(LongTermGoal goal, int maxTiles)
    {
        var result = new List<GoalPlanCandidate>();
        HashSet<string> protectedIds = goal.Constraints.PreserveItemIds.Select(EconomicItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int saleValue = Game1.player.Items.Where(p => p is StardewValley.Object o && TradeCrop(o) && !protectedIds.Contains(o.QualifiedItemId))
            .Sum(p => ((StardewValley.Object)p!).sellToStorePrice() * p!.Stack);
        int saleStacks = Game1.player.Items.Count(p => p is StardewValley.Object o && TradeCrop(o) && !protectedIds.Contains(o.QualifiedItemId));
        if (saleValue > 0)
            result.Add(new GoalPlanCandidate { Id = "sell_inventory_crops", Kind = "immediate_sale", Title = "보유 수확물 판매",
                ExpectedGold = saleValue, ExpectedProfit = saleValue, Days = 0, WorkUnits = Math.Max(1, saleStacks + 3), Confidence = "high",
                RequiredActions = new() { "sell_crops" }, Metadata = new() { ["sellableStackCount"] = saleStacks.ToString() } });
        result.AddRange(ReadExistingCropCandidates(maxTiles));
        foreach (CropOption option in ReadCropOptions(maxTiles, goal.Constraints.ReserveMoney))
        {
            if (option.Projection.ExpectedProfit < 0) continue;
            var actions = new List<string> { "farm_crops", "sell_crops" };
            if (option.PaidSeeds > 0) actions.Add("buy_seeds");
            result.Add(new GoalPlanCandidate
            {
                Id = "plant_" + option.SeedItemId.Replace("(", "").Replace(")", ""), Kind = "crop_cycle",
                Title = $"{option.SeedName} {option.Tiles}칸 재배", ExpectedGold = option.Projection.ExpectedRevenue,
                ExpectedProfit = option.Projection.ExpectedProfit, UpfrontCost = option.Projection.UpfrontCost,
                Days = option.GrowthDays, WorkUnits = option.Projection.WateringTileActions + option.Tiles * 3 + (option.PaidSeeds > 0 ? 4 : 0),
                Confidence = option.PaidSeeds > 0 ? "medium" : "high", RequiredActions = actions,
                Metadata = new(StringComparer.OrdinalIgnoreCase) {
                    ["seedItemId"] = option.SeedItemId, ["seedName"] = option.SeedName, ["harvestItemId"] = option.HarvestItemId,
                    ["tiles"] = option.Tiles.ToString(), ["ownedSeeds"] = option.AvailableSeeds.ToString(), ["paidSeeds"] = option.PaidSeeds.ToString(),
                    ["growthDays"] = option.GrowthDays.ToString(), ["regrowDays"] = option.RegrowDays.ToString(),
                    ["harvests"] = option.Projection.Harvests.ToString(), ["seedPrice"] = option.SeedPrice.ToString(),
                    ["harvestMethod"] = string.IsNullOrWhiteSpace(option.HarvestMethod) ? "Grab" : option.HarvestMethod
                }
            });
        }
        return result;
    }

    private List<GoalPlanCandidate> ReadExistingCropCandidates(int maxTiles)
    {
        var crops = Game1.getFarm().terrainFeatures.Pairs
            .Where(p => p.Value is HoeDirt h && h.crop != null && !h.crop.dead.Value)
            .Select(p =>
            {
                var crop = ((HoeDirt)p.Value).crop!;
                string harvestId = EconomicItemId(crop.indexOfHarvest.Value);
                int unitPrice = 0;
                string harvestName = harvestId;
                try
                {
                    Item item = ItemRegistry.Create(harvestId);
                    harvestName = item.DisplayName;
                    if (item is StardewValley.Object harvest) unitPrice = harvest.sellToStorePrice();
                }
                catch { }
                return new ExistingCropPlanTile { X = (int)p.Key.X, Y = (int)p.Key.Y, HarvestId = harvestId, HarvestName = harvestName,
                    UnitPrice = unitPrice, Days = ExistingCropDaysRemaining(crop),
                    HarvestMethod = crop.GetData()?.HarvestMethod.ToString() ?? "UNKNOWN" };
            })
            .Where(p => p.UnitPrice > 0 && p.Days >= 0)
            .ToList();
        var result = new List<GoalPlanCandidate>();
        int sequence = 0;
        foreach (var harvestGroup in crops.GroupBy(p => p.HarvestId, StringComparer.OrdinalIgnoreCase))
        {
            var remaining = harvestGroup.ToDictionary(p => (p.X, p.Y));
            while (remaining.Count > 0)
            {
                var start = remaining.Values.OrderBy(p => p.Y).ThenBy(p => p.X).First();
                var queue = new Queue<(int X, int Y)>();
                var component = new List<ExistingCropPlanTile>();
                queue.Enqueue((start.X, start.Y));
                while (queue.Count > 0)
                {
                    var key = queue.Dequeue();
                    if (!remaining.Remove(key, out var crop)) continue;
                    component.Add(crop);
                    foreach (var next in new[] { (key.X - 1, key.Y), (key.X + 1, key.Y), (key.X, key.Y - 1), (key.X, key.Y + 1) })
                        if (remaining.ContainsKey(next)) queue.Enqueue(next);
                }
                foreach (var chunk in ExistingCropChunks(component, maxTiles))
                {
                    int x = chunk.Min(p => p.X), y = chunk.Min(p => p.Y);
                    int width = chunk.Max(p => p.X) - x + 1, height = chunk.Max(p => p.Y) - y + 1;
                    int days = chunk.Max(p => p.Days), count = chunk.Count;
                    int expectedGold = chunk.Sum(p => p.UnitPrice);
                    ExistingCropPlanTile sample = chunk[0];
                    result.Add(new GoalPlanCandidate
                    {
                        Id = $"tend_existing_{sequence++:00}_{sample.HarvestId.ToString().Replace("(", "").Replace(")", "")}",
                        Kind = "existing_crop_cycle", Title = $"심어진 {sample.HarvestName} {count}칸 관리·수확",
                        ExpectedGold = expectedGold, ExpectedProfit = expectedGold, Days = days,
                        WorkUnits = Math.Max(1, count * Math.Max(1, days) + count + 3), Confidence = "high",
                        RequiredActions = new() { "tend_existing_crops", "sell_crops" },
                        Metadata = new(StringComparer.OrdinalIgnoreCase) { ["harvestItemId"] = sample.HarvestId,
                            ["tiles"] = count.ToString(), ["growthDays"] = days.ToString(), ["x"] = x.ToString(), ["y"] = y.ToString(),
                            ["width"] = width.ToString(), ["height"] = height.ToString(), ["harvestMethod"] = sample.HarvestMethod }
                    });
                }
            }
        }
        return result;
    }

    private static IEnumerable<List<ExistingCropPlanTile>> ExistingCropChunks(List<ExistingCropPlanTile> component, int maxTiles)
    {
        if (component.Count <= maxTiles)
        {
            int width = component.Max(p => p.X) - component.Min(p => p.X) + 1;
            int height = component.Max(p => p.Y) - component.Min(p => p.Y) + 1;
            if (width * height <= 64) { yield return component; yield break; }
        }
        foreach (ExistingCropPlanTile crop in component) yield return new List<ExistingCropPlanTile> { crop };
    }

    private static int ExistingCropDaysRemaining(Crop crop)
    {
        if (crop.phaseDays.Count == 0 || crop.dead.Value) return -1;
        bool ready = crop.currentPhase.Value >= crop.phaseDays.Count - 1 && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
        if (ready) return 0;
        if (crop.fullyGrown.Value) return GoalPlanPolicy.NormalizeGrowthDays(Math.Max(1, crop.dayOfCurrentPhase.Value));
        return GoalPlanPolicy.RemainingGrowthDays(crop.phaseDays.ToList(), crop.currentPhase.Value, crop.dayOfCurrentPhase.Value);
    }

    private static IEnumerable<GoalPlanCandidate> RankForResponse(IEnumerable<GoalPlanCandidate> candidates, LongTermGoal goal)
    {
        GoalPlanCandidate? first = GoalPlanPolicy.Select(candidates, goal.Constraints.StrategyPreference, goal.Progress.RemainingValue);
        return first == null ? candidates : new[] { first }.Concat(candidates.Where(p => p.Id != first.Id));
    }

    private GoalExecutionPlan CreateExecutionPlan(LongTermGoal goal, GoalPlanCandidate selected, string fingerprint, int maxTiles, int startOffset)
    {
        string now = DateTime.UtcNow.ToString("O");
        var plan = new GoalExecutionPlan
        {
            Revision = Math.Max(0, goal.Plan.Revision) + 1, Status = GoalPlanStatuses.Ready,
            StrategyId = selected.Id, StrategyKind = selected.Kind, StrategyTitle = selected.Title,
            Preference = goal.Constraints.StrategyPreference, ExpectedGold = selected.ExpectedGold,
            ExpectedProfit = selected.ExpectedProfit, UpfrontCost = selected.UpfrontCost, ExpectedDays = selected.Days + startOffset,
            MaxTiles = maxTiles, LatestWorkTime = goal.Constraints.LatestWorkTime,
            RemainingGoldAtPlanning = goal.Progress.RemainingValue, PlannedDayIndex = CurrentDayIndex(),
            PlannedGameDate = FarmDate(), PlannedGameTime = Game1.timeOfDay, StateFingerprint = fingerprint,
            GeneratedAtUtc = now, UpdatedAtUtc = now,
            Assumptions = new() { "Each step requires live preflight.", "Only actions already authorized on the goal may execute.",
                "Money and inventory changes make the saved plan stale and require replanning.", "Weather, shop conditions, routes and actual yields override projections.",
                startOffset == 0 ? "Planning may begin today." : "The latest work time has passed; planning begins next day." }
        };
        string previous = "";
        void Add(int offset, string action, string summary, bool conditional = false, Dictionary<string,string>? inputs = null)
        {
            if (plan.Steps.Count >= GoalPlanPolicy.MaxPlanSteps)
                throw new InvalidOperationException("PLAN_STEP_LIMIT_EXCEEDED");
            int sequence = plan.Steps.Count + 1;
            var step = new GoalPlanStep { Id = $"step-{sequence:00}", Sequence = sequence, DayIndex = CurrentDayIndex() + offset,
                DayLabel = offset == 0 ? "오늘" : $"{offset}일 후", Action = action, Summary = summary,
                Conditional = conditional, Inputs = inputs ?? new(StringComparer.OrdinalIgnoreCase) };
            step.Inputs.TryAdd("latestWorkTime", goal.Constraints.LatestWorkTime.ToString());
            if (previous != "") step.DependsOn.Add(previous);
            plan.Steps.Add(step); previous = step.Id;
        }
        if (selected.Kind == "immediate_sale")
        {
            Add(startOffset, "inspect_sellable_crops", "보호 품목을 제외한 판매 가능 수확물을 다시 관측");
            Add(startOffset, "get_shop_status", "피에르 영업 여부와 이동 가능 시간 확인");
            Add(startOffset, "sell_crop_stack", "관측·견적된 수확물만 판매하고 실제 골드 증가 검증");
            Add(startOffset, "verify_long_term_goal", "판매 후 실제 소지금으로 목표 진행률 재검증");
        }
        else if (selected.Kind == "existing_crop_cycle")
        {
            int x = ReadMetaInt(selected, "x"), y = ReadMetaInt(selected, "y"), width = ReadMetaInt(selected, "width"), height = ReadMetaInt(selected, "height");
            int growth = GoalPlanPolicy.NormalizeGrowthDays(ReadMetaInt(selected, "growthDays"));
            string harvestId = selected.Metadata["harvestItemId"];
            plan.Plot = new GoalPlanPlot { Location = "Farm", X = x, Y = y, Width = width, Height = height, BoundAtUtc = now };
            var cropInputs = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["harvestItemId"] = harvestId, ["existingCrops"] = "true",
                ["harvestMethod"] = selected.Metadata.TryGetValue("harvestMethod", out string? existingHarvestMethod) ? existingHarvestMethod : "UNKNOWN" };
            if (growth > 0) Add(startOffset, "water_plot", "이미 심어진 살아 있는 작물의 마른 칸만 물주기", conditional: true, inputs: new(cropInputs));
            for (int day = 1; day < growth; day++) Add(startOffset + day, "water_plot", "기존 작물이 성숙할 때까지 마른 칸만 물주기", conditional: true, inputs: new(cropInputs));
            Add(startOffset + growth, "harvest_plot", $"기존 작물이 성숙한 것을 확인하고 {cropInputs["harvestMethod"]} 방식으로 수확", inputs: new(cropInputs));
            Add(startOffset + growth, "get_shop_status", "피에르 영업 여부와 이동 가능 시간 확인");
            Add(startOffset + growth, "sell_crop_stack", "수확한 기존 작물을 실제 상점에서 판매하고 골드 증가 검증", inputs: new(){{"harvestItemId",harvestId}});
            Add(startOffset + growth, "verify_long_term_goal", "실제 소지금으로 목표 진행률 재검증");
        }
        else
        {
            int tiles = ReadMetaInt(selected, "tiles"), paidSeeds = ReadMetaInt(selected, "paidSeeds"), growth = GoalPlanPolicy.NormalizeGrowthDays(ReadMetaInt(selected, "growthDays"));
            string seedId = selected.Metadata["seedItemId"], harvestId = selected.Metadata["harvestItemId"];
            (int plotWidth, int plotHeight) = GoalPlanPolicy.RectangleForTiles(tiles);
            Add(startOffset, "select_farm_plot", $"{tiles}칸 농사 후보를 관측해 한 영역을 계획에 고정", inputs: new(){{"seedItemId",seedId},{"harvestItemId",harvestId},{"tiles",tiles.ToString()},{"width",plotWidth.ToString()},{"height",plotHeight.ToString()}});
            if (paidSeeds > 0) Add(startOffset, "buy_shop_item", $"예비금 {goal.Constraints.ReserveMoney}g를 보존하며 {seedId} 씨앗 {paidSeeds}개까지 구매", inputs: new(){{"seedItemId",seedId},{"quantity",paidSeeds.ToString()},{"maxTotalCost",(ReadMetaInt(selected,"seedPrice")*paidSeeds).ToString()},{"reserveMoney",goal.Constraints.ReserveMoney.ToString()}});
            Add(startOffset, "remove_dead_crops", "고정한 영역의 시든 작물을 낫으로 먼저 제거", conditional: true);
            Add(startOffset, "prepare_plot", "계획에 고정한 명시적 영역만 정리·경작");
            Add(startOffset, "plant_plot", $"{seedId} 씨앗을 빈 경작지에 파종", inputs: new(){{"seedItemId",seedId},{"tiles",tiles.ToString()}});
            Add(startOffset, "water_plot", "파종한 작물 영역의 마른 칸만 물주기");
            for (int day = 1; day < growth; day++) Add(startOffset + day, "water_plot", "살아 있는 미수확 작물만 물주기", conditional: true);
            string harvestMethod = selected.Metadata.TryGetValue("harvestMethod", out string? method) ? method : "UNKNOWN";
            Add(startOffset + growth, "harvest_plot", $"성숙 판정된 작물만 {harvestMethod} 방식으로 수확하고 수량 변화 검증", inputs: new(){{"harvestItemId",harvestId},{"harvestMethod",harvestMethod}});
            Add(startOffset + growth, "sell_crop_stack", "수확물을 실제 상점에서 견적 후 판매하고 골드 증가 검증", inputs: new(){{"harvestItemId",harvestId}});
            Add(startOffset + growth, "verify_long_term_goal", "실제 소지금으로 목표 진행률 재검증");
        }
        return plan;
    }

    private static int ReadMetaInt(GoalPlanCandidate candidate, string key)
        => candidate.Metadata.TryGetValue(key, out string? value) && int.TryParse(value, out int number) ? number : 0;

    private void SaveBlockedGoalPlan(LongTermGoal goal, string fingerprint, string reason, List<string> missing, GoalPlanCandidate? candidate = null, int maxTiles = 16)
    {
        string now = DateTime.UtcNow.ToString("O");
        goal.Plan = new GoalExecutionPlan { Revision = Math.Max(0, goal.Plan.Revision) + 1, Status = GoalPlanStatuses.Blocked,
            StrategyId = candidate?.Id ?? "", StrategyKind = candidate?.Kind ?? "", StrategyTitle = candidate?.Title ?? "",
            Preference = goal.Constraints.StrategyPreference, RemainingGoldAtPlanning = goal.Progress.RemainingValue,
            MaxTiles = maxTiles, LatestWorkTime = goal.Constraints.LatestWorkTime,
            PlannedDayIndex = CurrentDayIndex(), PlannedGameDate = FarmDate(), PlannedGameTime = Game1.timeOfDay,
            StateFingerprint = fingerprint, GeneratedAtUtc = now, UpdatedAtUtc = now, BlockedReason = reason,
            MissingAuthorizations = missing };
        TouchGoal(goal); _goalsDirty = true; FlushLongTermMemory();
    }

    private static bool GoalActionAuthorized(LongTermGoal goal, string action)
    {
        string[] broad = { "all", "all_actions", "autonomous_earning", "earn_money", "profit_strategy" };
        HashSet<string> allowed = goal.Constraints.AuthorizedActions.Select(NormalizeGoalAction).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (broad.Any(p => allowed.Contains(p))) return true;
        string[][] aliases = action switch
        {
            "sell_crops" => new[] { new[] { "sell_crops", "sell", "selling", "crop_sales", "trading" } },
            "buy_seeds" => new[] { new[] { "buy_seeds", "buy", "purchase", "shopping", "trading" } },
            "tend_existing_crops" => new[] { new[] { "tend_existing_crops", "care_existing_crops", "water_existing_crops", "harvest_existing_crops" } },
            "farm_crops" => new[] { new[] { "farm_crops", "farming", "crop_cycle", "grow_crops", "plant", "planting" } },
            _ => new[] { new[] { action } }
        };
        return aliases.SelectMany(p => p).Any(p => allowed.Contains(p));
    }

    private static string NormalizeGoalAction(string value) => (value ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private string GoalPlanFingerprint(LongTermGoal goal, int maxTiles)
    {
        HashSet<string> protectedIds = goal.Constraints.PreserveItemIds.Select(EconomicItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string inventory = string.Join("|", Game1.player.Items.Where(p => p != null && (protectedIds.Contains(p.QualifiedItemId)
                || p is StardewValley.Object o && (TradeCrop(o) || o.Category == StardewValley.Object.SeedsCategory)))
            .GroupBy(p => new { p!.QualifiedItemId, Quality = TradeQuality(p) })
            .OrderBy(p => p.Key.QualifiedItemId).ThenBy(p => p.Key.Quality)
            .Select(p => $"{p.Key.QualifiedItemId}:{p.Key.Quality}:{p.Sum(i => i!.Stack)}"));
        string raw = string.Join("\n", goal.Id, goal.Progress.RemainingValue, goal.Constraints.ReserveMoney,
            goal.Constraints.LatestWorkTime, goal.Constraints.StrategyPreference,
            string.Join("|", goal.Constraints.AuthorizedActions.OrderBy(p => p)),
            string.Join("|", goal.Constraints.PreserveItemIds.OrderBy(p => p)), Game1.player.Money,
            Game1.year, Game1.currentSeason, Game1.dayOfMonth, maxTiles, inventory, ExistingCropFingerprint());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string ExistingCropFingerprint() => string.Join("|", Game1.getFarm().terrainFeatures.Pairs
        .Where(p => p.Value is HoeDirt h && h.crop != null)
        .OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X)
        .Select(p => { var h = (HoeDirt)p.Value; var crop = h.crop!; return $"{(int)p.Key.X},{(int)p.Key.Y}:{crop.netSeedIndex.Value}:{crop.currentPhase.Value}:{crop.dayOfCurrentPhase.Value}:{crop.dead.Value}:{h.state.Value}"; }));

    public void RefreshGoalPlanFreshness()
    {
        if (!_memoryLoaded || !Context.IsWorldReady) return;
        bool changed = false;
        foreach (LongTermGoal goal in _goals.Goals.Where(p => p.Status == GoalStatuses.Active && p.Plan.Status == GoalPlanStatuses.Ready))
        {
            if (GoalPlanPolicy.HasSafeShape(goal.Plan)
                && goal.Plan.StateFingerprint == GoalPlanFingerprint(goal, Math.Clamp(goal.Plan.MaxTiles, 1, 64))) continue;
            goal.Plan.Status = GoalPlanStatuses.Stale;
            goal.Plan.BlockedReason = GoalPlanPolicy.HasSafeShape(goal.Plan) ? "ECONOMIC_STATE_CHANGED" : "PLAN_STEP_LIMIT_EXCEEDED";
            goal.Plan.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            TouchGoal(goal);
            changed = true;
        }
        if (!changed) return;
        _goalsDirty = true;
        FlushLongTermMemory();
    }
}
