using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private CommandResponse BuildGoalPlanCommand(GameCommand command, bool refresh)
    {
        RefreshLongTermGoalProgress();
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        if (goal.Status != GoalStatuses.Active) throw new InvalidOperationException("Only an active goal can receive an execution plan.");
        int requestedTiles = command.Params.ContainsKey("max_tiles") ? ShopInt(command, "max_tiles") : 0;
        int maxTiles = requestedTiles <= 0
            ? refresh && goal.Plan.MaxTiles > 0 ? Math.Clamp(goal.Plan.MaxTiles, 1, 64) : 16
            : Math.Clamp(requestedTiles, 1, 64);
        string fingerprint = GoalPlanFingerprint(goal, maxTiles);
        if (!refresh && goal.Plan.Status == GoalPlanStatuses.Ready && goal.Plan.StateFingerprint == fingerprint)
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
            return FarmReply(command, new { status = "BLOCKED", goalId = goal.Id, reason, plan = goal.Plan,
                requiresUserInput = false, note = "Observe new inventory, funds, season or supported capabilities before refreshing." });
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
            consideredCandidates = RankForResponse(deadlineEligible, goal).Take(8).ToList(),
            note = "The plan is persistent and read-only. Phase 3 does not execute steps. Live preflight and verified results are required before every future action." });
    }

    private CommandResponse InspectGoalPlanCommand(GameCommand command)
    {
        RefreshLongTermGoalProgress();
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        int requestedTiles = command.Params.ContainsKey("max_tiles") ? ShopInt(command, "max_tiles") : 0;
        int maxTiles = requestedTiles <= 0 ? Math.Clamp(goal.Plan.MaxTiles, 1, 64) : Math.Clamp(requestedTiles, 1, 64);
        bool stale = goal.Plan.Status != GoalPlanStatuses.None && goal.Plan.StateFingerprint != GoalPlanFingerprint(goal, maxTiles);
        string effectiveStatus = stale && goal.Plan.Status == GoalPlanStatuses.Ready ? GoalPlanStatuses.Stale : goal.Plan.Status;
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
                    ["harvests"] = option.Projection.Harvests.ToString(), ["seedPrice"] = option.SeedPrice.ToString()
                }
            });
        }
        return result;
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
        else
        {
            int tiles = ReadMetaInt(selected, "tiles"), paidSeeds = ReadMetaInt(selected, "paidSeeds"), growth = ReadMetaInt(selected, "growthDays");
            string seedId = selected.Metadata["seedItemId"];
            Add(startOffset, "analyze_farm_work", $"{tiles}칸 농사 후보와 도구·에너지·접근 가능 여부 재검사", inputs: new(){{"seedItemId",seedId},{"tiles",tiles.ToString()}});
            if (paidSeeds > 0) Add(startOffset, "buy_shop_item", $"예비금 {goal.Constraints.ReserveMoney}g를 보존하며 {seedId} 씨앗 {paidSeeds}개까지 구매", inputs: new(){{"seedItemId",seedId},{"quantity",paidSeeds.ToString()}});
            Add(startOffset, "prepare_plot", "관측으로 선택한 명시적 영역만 정리·경작");
            Add(startOffset, "plant_plot", $"{seedId} 씨앗을 빈 경작지에 파종", inputs: new(){{"seedItemId",seedId},{"tiles",tiles.ToString()}});
            Add(startOffset, "water_plot", "파종한 작물 영역의 마른 칸만 물주기");
            for (int day = 1; day < growth; day++) Add(startOffset + day, "water_plot", "살아 있는 미수확 작물만 물주기", conditional: true);
            Add(startOffset + growth, "harvest_plot", "성숙 판정된 작물만 수확하고 수량 변화 검증");
            Add(startOffset + growth, "sell_crop_stack", "수확물을 실제 상점에서 견적 후 판매하고 골드 증가 검증");
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
            Game1.year, Game1.currentSeason, Game1.dayOfMonth, maxTiles, inventory);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public void RefreshGoalPlanFreshness()
    {
        if (!_memoryLoaded || !Context.IsWorldReady) return;
        bool changed = false;
        foreach (LongTermGoal goal in _goals.Goals.Where(p => p.Status == GoalStatuses.Active && p.Plan.Status == GoalPlanStatuses.Ready))
        {
            if (goal.Plan.StateFingerprint == GoalPlanFingerprint(goal, Math.Clamp(goal.Plan.MaxTiles, 1, 64))) continue;
            goal.Plan.Status = GoalPlanStatuses.Stale;
            goal.Plan.BlockedReason = "ECONOMIC_STATE_CHANGED";
            goal.Plan.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            TouchGoal(goal);
            changed = true;
        }
        if (!changed) return;
        _goalsDirty = true;
        FlushLongTermMemory();
    }
}
