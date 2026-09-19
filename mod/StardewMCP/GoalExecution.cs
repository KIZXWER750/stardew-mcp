using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;

namespace StardewMCP;

public partial class CommandExecutor
{
    private readonly HashSet<string> _recentGoalPlotCandidates = new(StringComparer.Ordinal);
    private DateTime _recentGoalPlotCandidatesAtUtc = DateTime.MinValue;

    private CommandResponse StartGoalPlanExecutionCommand(GameCommand command)
    {
        RefreshLongTermGoalProgress();
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        if (goal.Status != GoalStatuses.Active) throw new InvalidOperationException("Only an active goal can execute a plan.");
        if (goal.Plan.Status == GoalPlanStatuses.Ready)
        {
            string current = GoalPlanFingerprint(goal, Math.Clamp(goal.Plan.MaxTiles, 1, 64));
            if (!goal.Plan.StateFingerprint.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                goal.Plan.Status = GoalPlanStatuses.Stale;
                goal.Plan.BlockedReason = "STATE_CHANGED_BEFORE_EXECUTION";
                SaveGoalExecution(goal);
                return FarmReply(command, new { status = "STALE", goalId = goal.Id, reason = goal.Plan.BlockedReason,
                    note = "Call refresh_goal_plan before execution." });
            }
            EnsurePlanActionsAuthorized(goal);
            goal.Plan.Status = GoalPlanStatuses.Executing;
            goal.Plan.ExecutionStartedAtUtc = DateTime.UtcNow.ToString("O");
            goal.Plan.BlockedReason = "";
            SaveGoalExecution(goal);
        }
        else if (goal.Plan.Status is not (GoalPlanStatuses.Executing or GoalPlanStatuses.Waiting))
            throw new InvalidOperationException($"Plan status '{goal.Plan.Status}' cannot execute. Build, refresh or resume it first.");
        return NextGoalPlanDirective(command, goal);
    }

    private CommandResponse ContinueGoalPlanExecutionCommand(GameCommand command)
    {
        RefreshLongTermGoalProgress();
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        if (goal.Status != GoalStatuses.Active) throw new InvalidOperationException("Only an active goal can continue execution.");
        if (goal.Plan.Status is not (GoalPlanStatuses.Executing or GoalPlanStatuses.Waiting))
            throw new InvalidOperationException($"Plan status '{goal.Plan.Status}' is not running.");
        EnsurePlanActionsAuthorized(goal);
        return NextGoalPlanDirective(command, goal);
    }

    private CommandResponse BindGoalPlanPlotCommand(GameCommand command)
    {
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        GoalPlanStep step = FindLeasedStep(goal, ShopText(command, "step_id"), ShopText(command, "lease_id"));
        if (step.Action != "select_farm_plot") throw new InvalidOperationException("The leased step is not plot selection.");
        string location = ShopText(command, "location");
        int x = ShopInt(command, "x"), y = ShopInt(command, "y"), width = ShopInt(command, "width"), height = ShopInt(command, "height");
        if (location != "Farm" || Game1.currentLocation?.Name != "Farm") throw new InvalidOperationException("Plot binding requires the player on Farm.");
        if (x < 0 || y < 0 || width < 1 || height < 1 || width * height > Math.Clamp(goal.Plan.MaxTiles, 1, 64))
            throw new InvalidOperationException("The plot must be nonnegative and within the planned tile limit.");
        if ((DateTime.UtcNow - _recentGoalPlotCandidatesAtUtc).TotalMinutes > 5 || !_recentGoalPlotCandidates.Contains(PlotKey(x, y, width, height)))
            throw new InvalidOperationException("Bind only a fresh rectangle returned by find_plot_candidates.");
        int requestedTiles = StepInt(step, "tiles");
        if (width * height < requestedTiles) throw new InvalidOperationException("The bound plot is smaller than the planned crop count.");
        if (Game1.getFarm() is not Farm farm || x + width > farm.Map.Layers[0].LayerWidth || y + height > farm.Map.Layers[0].LayerHeight)
            throw new InvalidOperationException("The plot extends outside the Farm map.");
        goal.Plan.Plot = new GoalPlanPlot { Location = location, X = x, Y = y, Width = width, Height = height, BoundAtUtc = DateTime.UtcNow.ToString("O") };
        foreach (GoalPlanStep future in goal.Plan.Steps.Where(p => p.Sequence > step.Sequence && IsFarmStep(p.Action)))
        {
            future.Inputs["location"] = location; future.Inputs["x"] = x.ToString(); future.Inputs["y"] = y.ToString();
            future.Inputs["width"] = width.ToString(); future.Inputs["height"] = height.ToString();
        }
        CompleteStep(step, "Verified candidate bound to persistent plan plot.", skipped: false);
        SaveGoalExecution(goal);
        return NextGoalPlanDirective(command, goal);
    }

    private CommandResponse ReportGoalPlanStepCommand(GameCommand command)
    {
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        GoalPlanStep step = FindLeasedStep(goal, ShopText(command, "step_id"), ShopText(command, "lease_id"), allowCompletedGoal: true);
        string outcome = ShopText(command, "outcome").Trim().ToLowerInvariant();
        string summary = ShopText(command, "result_summary").Trim();
        if (summary.Length is < 1 or > 4000) throw new InvalidOperationException("result_summary must be 1..4000 characters.");
        if (outcome == "completed")
        {
            VerifyCompletedPlanStep(goal, step, summary);
            CompleteStep(step, summary, skipped: false);
        }
        else if (outcome == "skipped")
        {
            if (!step.Conditional || !CanSkipPlanStep(goal, step)) throw new InvalidOperationException("Only a satisfied conditional step may be skipped.");
            CompleteStep(step, summary, skipped: true);
        }
        else if (outcome == "paused")
        {
            ResetStepForRetry(step, summary);
            SchedulePausedShopStep(step);
            goal.Plan.Status = GoalPlanStatuses.Waiting;
            goal.Plan.BlockedReason = "STEP_PAUSED";
            SaveGoalExecution(goal);
            return FarmReply(command, new { status = "WAITING", goalId = goal.Id, stepId = step.Id, reason = summary,
                note = "The same persisted step remains pending. Recover safely or let the next day auto-resume it." });
        }
        else if (outcome == "blocked")
        {
            step.Status = GoalPlanStepStatuses.Blocked; step.BlockedReason = summary; step.ResultSummary = summary; step.LeaseId = "";
            goal.Plan.Status = GoalPlanStatuses.Blocked; goal.Plan.BlockedReason = "STEP_BLOCKED:" + step.Id;
            goal.Status = GoalStatuses.Blocked; goal.BlockedReason = goal.Plan.BlockedReason;
            SaveGoalExecution(goal);
            return FarmReply(command, new { status = "BLOCKED", goalId = goal.Id, stepId = step.Id, reason = summary,
                note = "Resume only after the blocking condition changes; completed world changes are retained." });
        }
        else throw new InvalidOperationException("outcome must be completed, skipped, paused or blocked.");

        RefreshLongTermGoalProgress();
        if (goal.Progress.SuccessConditionMet)
        {
            goal.Plan.Status = GoalPlanStatuses.Completed;
            goal.Plan.BlockedReason = "";
            SaveGoalExecution(goal);
            return FarmReply(command, new { status = "GOAL_COMPLETED", goalId = goal.Id, plan = goal.Plan, progress = goal.Progress });
        }
        SaveGoalExecution(goal);
        return NextGoalPlanDirective(command, goal);
    }

    private CommandResponse NextGoalPlanDirective(GameCommand command, LongTermGoal goal)
    {
        if (goal.Progress.SuccessConditionMet)
        {
            goal.Plan.Status = GoalPlanStatuses.Completed;
            SaveGoalExecution(goal);
            return FarmReply(command, new { status = "GOAL_COMPLETED", goalId = goal.Id, progress = goal.Progress });
        }
        GoalPlanStep? step = goal.Plan.Steps.OrderBy(p => p.Sequence)
            .FirstOrDefault(p => p.Status is not (GoalPlanStepStatuses.Completed or GoalPlanStepStatuses.Skipped));
        if (step == null)
        {
            goal.Plan.Status = GoalPlanStatuses.Stale;
            goal.Plan.BlockedReason = "CYCLE_COMPLETE_REPLAN_REQUIRED";
            SaveGoalExecution(goal);
            return FarmReply(command, new { status = "REPLAN_REQUIRED", goalId = goal.Id, progress = goal.Progress,
                note = "This verified strategy cycle ended without reaching the target. Call refresh_goal_plan, then start_goal_plan_execution." });
        }
        if (step.Status == GoalPlanStepStatuses.Blocked)
            return FarmReply(command, new { status = "BLOCKED", goalId = goal.Id, step, reason = step.BlockedReason });
        if (step.DependsOn.Any(id => !goal.Plan.Steps.Any(p => p.Id == id && p.Status is GoalPlanStepStatuses.Completed or GoalPlanStepStatuses.Skipped)))
            throw new InvalidOperationException("Plan dependency is not complete.");
        int today = CurrentDayIndex();
        if (step.DayIndex > today || step.DayIndex == today && step.NotBeforeTime > 0 && Game1.timeOfDay < step.NotBeforeTime
            || Game1.timeOfDay >= goal.Plan.LatestWorkTime)
        {
            goal.Plan.Status = GoalPlanStatuses.Waiting;
            goal.Plan.BlockedReason = step.DayIndex > today ? "WAITING_FOR_PLANNED_DAY"
                : step.NotBeforeTime > 0 && Game1.timeOfDay < step.NotBeforeTime ? "WAITING_FOR_START_TIME" : "LATEST_WORK_TIME_REACHED";
            SaveGoalExecution(goal);
            return FarmReply(command, new { status = "WAITING", goalId = goal.Id, nextStep = step, currentDayIndex = today,
                reason = goal.Plan.BlockedReason, autoResume = true, note = "The persisted plan will be offered to the agent again when due." });
        }
        if (step.Status == GoalPlanStepStatuses.InProgress && TryReconcileInterruptedStep(goal, step))
        {
            CompleteStep(step, "Recovered from interruption: the planned world-state change is already verified.", skipped: false);
            SaveGoalExecution(goal);
            return NextGoalPlanDirective(command, goal);
        }
        if (step.Status != GoalPlanStepStatuses.InProgress)
        {
            CaptureBeforeSnapshot(goal, step);
            step.Status = GoalPlanStepStatuses.InProgress;
            step.LeaseId = Guid.NewGuid().ToString("N");
            step.ClaimedAtUtc = DateTime.UtcNow.ToString("O");
            step.AttemptCount++;
        }
        goal.Plan.Status = GoalPlanStatuses.Executing;
        goal.Plan.BlockedReason = "";
        SaveGoalExecution(goal);
        return FarmReply(command, new { status = "EXECUTE_STEP", goalId = goal.Id, planRevision = goal.Plan.Revision,
            step, execution = PlanStepExecutionSpec(goal, step),
            note = "Execute only this leased step with normal gameplay tools. Then report the verified result with report_goal_plan_step; continue until WAITING, BLOCKED, REPLAN_REQUIRED or GOAL_COMPLETED." });
    }

    private object PlanStepExecutionSpec(LongTermGoal goal, GoalPlanStep step)
    {
        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in step.Inputs)
        {
            if (pair.Key is "harvestItemId" or "tiles") continue;
            if (pair.Key == "seedItemId" && step.Action is not ("buy_shop_item" or "plant_plot")) continue;
            if (pair.Key == "latestWorkTime" && !IsFarmStep(step.Action)) continue;
            string key = pair.Key switch { "seedItemId" when step.Action == "buy_shop_item" => "item_id", "seedItemId" => "seed_item_id",
                "maxTotalCost" => "max_total_cost", "reserveMoney" => "reserve_money", "latestWorkTime" => "stop_time", _ => pair.Key };
            parameters[key] = pair.Value;
        }
        if (step.Action == "select_farm_plot")
        {
            parameters["location"] = "Farm"; parameters["anchor_x"] = (int)Game1.player.Tile.X; parameters["anchor_y"] = (int)Game1.player.Tile.Y;
            parameters["direction"] = "ANY"; parameters["search_radius"] = 8; parameters["max_candidates"] = 8;
        }
        if (step.Action == "verify_long_term_goal") parameters["goal_id"] = goal.Id;
        if (goal.Plan.Plot != null && IsFarmStep(step.Action))
        {
            parameters["location"] = goal.Plan.Plot.Location; parameters["x"] = goal.Plan.Plot.X; parameters["y"] = goal.Plan.Plot.Y;
            parameters["width"] = goal.Plan.Plot.Width; parameters["height"] = goal.Plan.Plot.Height;
            parameters["stop_time"] = goal.Plan.LatestWorkTime;
            if (step.Action == "water_plot") parameters["target_filter"] = "CROPS_ONLY";
        }
        string guidance = step.Action switch
        {
            "select_farm_plot" => "Call find_plot_candidates with the exact planned width/height, choose one returned reachable candidate, then call bind_goal_plan_plot with this lease.",
            "buy_shop_item" => "Check shop status, follow observed route, open and inspect Pierre's shop, then buy the exact seed and quantity within reserve money. Close the shop afterward.",
            "sell_crop_stack" => "Inspect fresh sellable crop quotes, check/open Pierre's shop, sell only authorized unprotected whole stacks, close the shop, and verify money increased.",
            "inspect_sellable_crops" => "Call inspect_sellable_crops and retain its exact observed candidates for the sale stage.",
            "get_shop_status" => "Call get_shop_status. If closed, report paused rather than forcing entry or sleeping without authorization.",
            "verify_long_term_goal" => "Call verify_long_term_goal for this exact goal ID and report its live result.",
            _ => $"Call {step.Action} with the bound parameters. Follow recovery hints only inside the authorized scope."
        };
        return new { tool = step.Action == "select_farm_plot" ? "find_plot_candidates" : step.Action, parameters, guidance };
    }

    private void VerifyCompletedPlanStep(LongTermGoal goal, GoalPlanStep step, string summary)
    {
        string lower = summary.ToLowerInvariant();
        if (lower.Contains("task_blocked") || lower.Contains("failed") || lower.Contains("uncertain") || lower.Contains("paused"))
            throw new InvalidOperationException("A blocked, failed, uncertain or paused tool result cannot complete a plan step.");
        GoalFarmSnapshot now = CaptureFarmSnapshot(goal);
        string seedId = StepText(step, "seedItemId"), harvestId = StepText(step, "harvestItemId");
        switch (step.Action)
        {
            case "buy_shop_item":
                int bought = InventoryQuantity(seedId) - step.BeforeSeedQuantity;
                int spent = step.BeforeMoney - Game1.player.Money;
                if (bought < StepInt(step, "quantity") || spent < 0 || spent > StepInt(step, "maxTotalCost") || Game1.player.Money < goal.Constraints.ReserveMoney)
                    throw new InvalidOperationException("Exact seed quantity, spending cap and reserve money were not verified.");
                break;
            case "prepare_plot":
                if (now.PreparedTiles < Math.Max(step.BeforePreparedTiles, goal.Plan.Plot?.Width * goal.Plan.Plot?.Height ?? 0))
                    throw new InvalidOperationException("The entire bound plot is not verified as prepared soil/crops.");
                break;
            case "plant_plot":
                if (now.CropTiles - step.BeforeCropTiles < StepInt(step, "tiles")) throw new InvalidOperationException("The planned crop count was not verified in the bound plot.");
                break;
            case "water_plot":
                if (now.DryCropTiles > 0) throw new InvalidOperationException("Dry live crops remain in the bound plot.");
                break;
            case "harvest_plot":
                if (InventoryQuantity(harvestId) <= step.BeforeHarvestQuantity && now.ReadyCropTiles >= step.BeforeReadyCropTiles)
                    throw new InvalidOperationException("Neither harvested inventory nor fewer ready crops was verified.");
                break;
            case "sell_crop_stack":
                int currentSaleQuantity = string.IsNullOrWhiteSpace(harvestId) ? SellableCropQuantity(goal) : InventoryQuantity(harvestId);
                int previousSaleQuantity = string.IsNullOrWhiteSpace(harvestId) ? step.BeforeSellableCropQuantity : step.BeforeHarvestQuantity;
                GoalProgress live = GoalSchema.EvaluateMoney(goal, Game1.player.Money, CurrentDayIndex(), FarmDate(), Game1.timeOfDay);
                if (Game1.player.Money <= step.BeforeMoney || currentSaleQuantity >= previousSaleQuantity || currentSaleQuantity > 0 && !live.SuccessConditionMet)
                    throw new InvalidOperationException("Crop decrease, money increase, and either exhausted sale scope or achieved goal are required to verify a sale stage.");
                break;
        }
    }

    private bool TryReconcileInterruptedStep(LongTermGoal goal, GoalPlanStep step)
    {
        if (step.Action is not ("buy_shop_item" or "prepare_plot" or "plant_plot" or "water_plot" or "harvest_plot" or "sell_crop_stack")) return false;
        try { VerifyCompletedPlanStep(goal, step, "reconciled verified state"); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private bool CanSkipPlanStep(LongTermGoal goal, GoalPlanStep step)
    {
        GoalFarmSnapshot now = CaptureFarmSnapshot(goal);
        return step.Action == "water_plot" && now.DryCropTiles == 0;
    }

    private void CaptureBeforeSnapshot(LongTermGoal goal, GoalPlanStep step)
    {
        GoalFarmSnapshot farm = CaptureFarmSnapshot(goal);
        step.BeforeMoney = Game1.player.Money;
        step.BeforeSeedQuantity = InventoryQuantity(StepText(step, "seedItemId"));
        step.BeforeHarvestQuantity = InventoryQuantity(StepText(step, "harvestItemId"));
        step.BeforePreparedTiles = farm.PreparedTiles; step.BeforeCropTiles = farm.CropTiles;
        step.BeforeReadyCropTiles = farm.ReadyCropTiles; step.BeforeDryCropTiles = farm.DryCropTiles;
        step.BeforeSellableCropQuantity = SellableCropQuantity(goal);
    }

    private GoalFarmSnapshot CaptureFarmSnapshot(LongTermGoal goal)
    {
        var result = new GoalFarmSnapshot();
        GoalPlanPlot? plot = goal.Plan.Plot;
        if (plot == null || Game1.getFarm() is not Farm farm) return result;
        for (int y = plot.Y; y < plot.Y + plot.Height; y++) for (int x = plot.X; x < plot.X + plot.Width; x++)
        {
            if (!farm.terrainFeatures.TryGetValue(new Vector2(x, y), out var feature) || feature is not HoeDirt dirt) continue;
            result.PreparedTiles++;
            if (dirt.crop == null) continue;
            result.CropTiles++;
            if (dirt.state.Value != 1) result.DryCropTiles++;
            var crop = dirt.crop;
            if (!crop.dead.Value && crop.phaseDays.Count > 0 && crop.currentPhase.Value >= crop.phaseDays.Count - 1
                && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0)) result.ReadyCropTiles++;
        }
        return result;
    }

    private int InventoryQuantity(string qualifiedId) => string.IsNullOrWhiteSpace(qualifiedId) ? 0
        : Game1.player.Items.Where(p => p?.QualifiedItemId == qualifiedId).Sum(p => p!.Stack);

    private int SellableCropQuantity(LongTermGoal goal)
    {
        HashSet<string> protectedIds = goal.Constraints.PreserveItemIds.Select(EconomicItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Game1.player.Items.Where(p => p is StardewValley.Object o && TradeCrop(o) && !protectedIds.Contains(o.QualifiedItemId)).Sum(p => p!.Stack);
    }

    private void EnsurePlanActionsAuthorized(LongTermGoal goal)
    {
        var missing = goal.Plan.Steps.Select(RequiredGoalAction).Where(p => p != "" && !GoalActionAuthorized(goal, p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("Plan actions are no longer authorized: " + string.Join(", ", missing));
    }

    private static string RequiredGoalAction(GoalPlanStep step) => step.Action switch
    {
        "buy_shop_item" => "buy_seeds",
        "prepare_plot" or "plant_plot" or "water_plot" or "harvest_plot" or "select_farm_plot" => "farm_crops",
        "sell_crop_stack" => "sell_crops",
        _ => ""
    };

    private GoalPlanStep FindLeasedStep(LongTermGoal goal, string stepId, string leaseId, bool allowCompletedGoal = false)
    {
        bool completionRace = allowCompletedGoal && goal.Status == GoalStatuses.Completed && goal.Plan.Status == GoalPlanStatuses.Completed;
        if (!completionRace && (goal.Status != GoalStatuses.Active || goal.Plan.Status != GoalPlanStatuses.Executing))
            throw new InvalidOperationException("The goal plan is not actively executing.");
        GoalPlanStep step = goal.Plan.Steps.FirstOrDefault(p => p.Id.Equals(stepId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown plan step_id.");
        if (step.Status != GoalPlanStepStatuses.InProgress || string.IsNullOrWhiteSpace(step.LeaseId)
            || !step.LeaseId.Equals(leaseId, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid or expired plan step lease.");
        return step;
    }

    private static bool IsFarmStep(string action) => action is "prepare_plot" or "plant_plot" or "water_plot" or "harvest_plot";
    private static string PlotKey(int x, int y, int width, int height) => $"{x},{y},{width},{height}";

    private void RememberGoalPlotCandidates(IEnumerable<PlotCandidate> candidates)
    {
        _recentGoalPlotCandidates.Clear();
        foreach (PlotCandidate candidate in candidates) _recentGoalPlotCandidates.Add(PlotKey(candidate.X, candidate.Y, candidate.Width, candidate.Height));
        _recentGoalPlotCandidatesAtUtc = DateTime.UtcNow;
    }
    private static string StepText(GoalPlanStep step, string key) => step.Inputs.TryGetValue(key, out string? value) ? value : "";
    private static int StepInt(GoalPlanStep step, string key) => int.TryParse(StepText(step, key), out int value) ? value : 0;

    private static void CompleteStep(GoalPlanStep step, string summary, bool skipped)
    {
        step.Status = skipped ? GoalPlanStepStatuses.Skipped : GoalPlanStepStatuses.Completed;
        step.ResultSummary = summary; step.CompletedAtUtc = DateTime.UtcNow.ToString("O"); step.LeaseId = ""; step.BlockedReason = "";
    }

    private static void ResetStepForRetry(GoalPlanStep step, string summary)
    {
        step.Status = GoalPlanStepStatuses.Pending; step.ResultSummary = summary; step.LeaseId = ""; step.BlockedReason = "";
    }

    private void SchedulePausedShopStep(GoalPlanStep step)
    {
        if (step.Action is not ("get_shop_status" or "buy_shop_item" or "sell_crop_stack")) return;
        PierreStatus shop = ReadPierreStatus();
        if (shop.CanAttemptTrade) return;
        step.NotBeforeTime = shop.TradeOpens;
        if (shop.Reason != "BEFORE_09_00") step.DayIndex = Math.Max(step.DayIndex, CurrentDayIndex() + 1);
    }

    private void SaveGoalExecution(LongTermGoal goal)
    {
        goal.Plan.UpdatedAtUtc = DateTime.UtcNow.ToString("O"); goal.Plan.LastExecutionAtUtc = goal.Plan.UpdatedAtUtc;
        TouchGoal(goal); _goalsDirty = true; FlushLongTermMemory();
    }

    public string GetPendingGoalPlanExecutionPrompt()
    {
        if (!_memoryLoaded || !Context.IsWorldReady) return "";
        LongTermGoal? goal = _goals.Goals.FirstOrDefault(p => p.Status == GoalStatuses.Active
            && p.Plan.Status is GoalPlanStatuses.Executing or GoalPlanStatuses.Waiting);
        if (goal == null) return "";
        GoalPlanStep? step = goal.Plan.Steps.OrderBy(p => p.Sequence)
            .FirstOrDefault(p => p.Status is not (GoalPlanStepStatuses.Completed or GoalPlanStepStatuses.Skipped));
        if (step == null || step.DayIndex > CurrentDayIndex() || step.DayIndex == CurrentDayIndex() && step.NotBeforeTime > 0 && Game1.timeOfDay < step.NotBeforeTime
            || Game1.timeOfDay >= goal.Plan.LatestWorkTime) return "";
        if (goal.Plan.LastDispatchDayIndex == CurrentDayIndex() && goal.Plan.LastDispatchStepId == step.Id) return "";
        return "CONTINUE PERSISTENT GOAL PLAN\nGoal ID: " + goal.Id + "\nPlan revision: " + goal.Plan.Revision
            + "\nCall continue_goal_plan_execution for this exact goal. Execute only leased due steps, report each verified result, and continue until WAITING, BLOCKED, REPLAN_REQUIRED or GOAL_COMPLETED.";
    }

    public void MarkGoalPlanExecutionDispatched()
    {
        LongTermGoal? goal = _goals.Goals.FirstOrDefault(p => p.Status == GoalStatuses.Active
            && p.Plan.Status is GoalPlanStatuses.Executing or GoalPlanStatuses.Waiting);
        GoalPlanStep? step = goal?.Plan.Steps.OrderBy(p => p.Sequence)
            .FirstOrDefault(p => p.Status is not (GoalPlanStepStatuses.Completed or GoalPlanStepStatuses.Skipped));
        if (goal == null || step == null) return;
        goal.Plan.LastDispatchDayIndex = CurrentDayIndex(); goal.Plan.LastDispatchStepId = step.Id; SaveGoalExecution(goal);
    }

    private sealed class GoalFarmSnapshot
    {
        public int PreparedTiles { get; set; }
        public int CropTiles { get; set; }
        public int ReadyCropTiles { get; set; }
        public int DryCropTiles { get; set; }
    }
}
