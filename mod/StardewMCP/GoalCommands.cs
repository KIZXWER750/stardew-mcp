using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private CommandResponse CreateLongTermGoalCommand(GameCommand command)
    {
        string kind = ShopText(command, "kind").Trim().ToLowerInvariant();
        if (kind == "") kind = GoalKinds.MoneyTarget;
        if (kind != GoalKinds.MoneyTarget) throw new InvalidOperationException("The current goal schema supports only money_target goals.");
        string summary = ShopText(command, "summary").Trim();
        string metric = ShopText(command, "metric").Trim();
        if (metric == "") metric = MoneyGoalMetrics.CurrentBalance;
        int target = ShopInt(command, "target_value");
        int reserve = command.Params.ContainsKey("reserve_money") ? ShopInt(command, "reserve_money") : 0;
        int latest = command.Params.ContainsKey("latest_work_time") ? ShopInt(command, "latest_work_time") : 2200;
        int? deadline = command.Params.ContainsKey("deadline_day_index") ? ShopInt(command, "deadline_day_index") : null;
        LongTermGoal goal = CreateMoneyGoal(summary, metric, target, reserve, latest,
            ShopText(command, "strategy_preference"), ReadStringList(command, "authorized_actions"),
            ReadStringList(command, "preserve_item_ids"), deadline, ShopText(command, "deadline_label"),
            ReadCommandBool(command, "allow_daily_return_home"), ReadCommandBool(command, "allow_daily_sleep"));
        return FarmReply(command, new
        {
            status = "SAVED", goal, currentMoney = Game1.player.Money, dayIndex = (int)Game1.stats.DaysPlayed,
            note = "The goal is persistent. Phase 4 can select, save and execute authorized supported plan steps. Completion is verified from live money."
        });
    }

    private CommandResponse ListLongTermGoalsCommand(GameCommand command)
    {
        string status = ShopText(command, "status").Trim();
        IReadOnlyList<LongTermGoal> goals = GetLongTermGoals(status == "" ? null : status);
        return FarmReply(command, new { status = "OBSERVED", count = goals.Count, goals });
    }

    private CommandResponse InspectLongTermGoalCommand(GameCommand command)
    {
        string id = ShopText(command, "goal_id").Trim();
        LongTermGoal goal = GetLongTermGoals().FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown long-term goal_id.");
        return FarmReply(command, new { status = "OBSERVED", goal, currentMoney = Game1.player.Money,
            note = "Success is based on live game money, never on model claims." });
    }

    private CommandResponse TransitionLongTermGoalCommand(GameCommand command)
    {
        string status = ShopText(command, "status").Trim();
        if (status is not (GoalStatuses.Active or GoalStatuses.Paused or GoalStatuses.Cancelled))
            throw new InvalidOperationException("Goal status command supports active, paused or cancelled.");
        LongTermGoal goal = TransitionLongTermGoal(ShopText(command, "goal_id"), status, ShopText(command, "reason"));
        return FarmReply(command, new { status = "SAVED", goal });
    }

    private CommandResponse VerifyLongTermGoalCommand(GameCommand command)
    {
        RefreshLongTermGoalProgress();
        return InspectLongTermGoalCommand(command);
    }

    private CommandResponse SetGoalDailyLifePolicyCommand(GameCommand command)
    {
        LongTermGoal goal = SetGoalDailyLifePolicy(ShopText(command, "goal_id"),
            ReadCommandBool(command, "allow_daily_return_home"), ReadCommandBool(command, "allow_daily_sleep"));
        return FarmReply(command, new { status = "SAVED", goalId = goal.Id, goal.Constraints.AllowDailyReturnHome,
            goal.Constraints.AllowDailySleep, note = "The policy persists across days and restarts. No gameplay action was executed." });
    }

    private CommandResponse ScheduleGoalWakeupCommand(GameCommand command)
    {
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        if (GoalStatuses.IsTerminal(goal.Status)) throw new InvalidOperationException("Cannot schedule a wakeup for a terminal goal.");
        string prompt = ShopText(command, "prompt").Trim();
        if (prompt.Length is < 1 or > 2000) throw new InvalidOperationException("Wakeup prompt must be 1..2000 characters.");
        int day = command.Params.ContainsKey("not_before_day_index") ? ShopInt(command, "not_before_day_index") : CurrentDayIndex();
        int time = command.Params.ContainsKey("not_before_time") ? ShopInt(command, "not_before_time") : Game1.timeOfDay;
        int energy = command.Params.ContainsKey("minimum_energy") ? ShopInt(command, "minimum_energy") : 0;
        string location = ShopText(command, "required_location").Trim();
        if (day < CurrentDayIndex() || time < 600 || time > 2600 || time % 100 >= 60 || energy < 0)
            throw new InvalidOperationException("Invalid wakeup day, time, or energy condition.");
        int requestedTime = time;
        bool pierreTravelLeadApplied = (prompt.Contains("Pierre", StringComparison.OrdinalIgnoreCase) || prompt.Contains("피에르"))
            && time == 900 && !location.Equals("SeedShop", StringComparison.OrdinalIgnoreCase);
        if (pierreTravelLeadApplied) { time = 830; location = ""; }
        foreach (GoalWakeup old in _goals.Wakeups.Where(p => p.GoalId.Equals(goal.Id, StringComparison.OrdinalIgnoreCase)
            && p.Status == "scheduled" && GoalQuestionPolicy.Fingerprint(p.Prompt) == GoalQuestionPolicy.Fingerprint(prompt)))
            old.Status = "cancelled";
        var wakeup = new GoalWakeup { GoalId = goal.Id, Prompt = prompt, NotBeforeDayIndex = day,
            NotBeforeTime = time, MinimumEnergy = energy, RequiredLocation = location,
            CreatedAtUtc = DateTime.UtcNow.ToString("O") };
        _goals.Wakeups.Add(wakeup); _goalsDirty = true; FlushLongTermMemory();
        return FarmReply(command, new { status = "SCHEDULED", wakeup, requestedTime, pierreTravelLeadApplied,
            note = "The in-game host will invoke the AI once when every saved condition is true, including after a restart." });
    }

    private CommandResponse CancelGoalWakeupCommand(GameCommand command)
    {
        string id = ShopText(command, "wakeup_id").Trim();
        GoalWakeup wakeup = _goals.Wakeups.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown wakeup_id.");
        wakeup.Status = "cancelled"; _goalsDirty = true; FlushLongTermMemory();
        return FarmReply(command, new { status = "CANCELLED", wakeupId = wakeup.Id });
    }

    public string GetDueGoalWakeupPrompt()
    {
        if (!_memoryLoaded || !Context.IsWorldReady) return "";
        int today = CurrentDayIndex();
        GoalWakeup? wakeup = _goals.Wakeups.Where(p => p.Status == "scheduled" && p.NotBeforeDayIndex <= today
                && (p.NotBeforeDayIndex < today || Game1.timeOfDay >= p.NotBeforeTime)
                && Game1.player.Stamina >= p.MinimumEnergy
                && (p.RequiredLocation == "" || p.RequiredLocation.Equals(Game1.currentLocation?.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.NotBeforeDayIndex).ThenBy(p => p.NotBeforeTime).FirstOrDefault();
        if (wakeup == null) return "";
        LongTermGoal? goal = _goals.Goals.FirstOrDefault(p => p.Id.Equals(wakeup.GoalId, StringComparison.OrdinalIgnoreCase));
        if (goal == null || GoalStatuses.IsTerminal(goal.Status)) { wakeup.Status = "cancelled"; _goalsDirty = true; FlushLongTermMemory(); return ""; }
        return "PERSISTENT AI WAKEUP\nWakeup ID: " + wakeup.Id + "\nGoal ID: " + wakeup.GoalId
            + "\nSaved prompt: " + wakeup.Prompt
            + "\nThe in-game host verified the saved day/time/energy/location conditions. Inspect live state and the goal, then continue autonomously within its saved permissions. Do not ask about routine implementation choices.";
    }

    public void MarkGoalWakeupDispatched()
    {
        string prompt = GetDueGoalWakeupPrompt();
        if (prompt == "") return;
        string id = prompt.Split('\n').FirstOrDefault(p => p.StartsWith("Wakeup ID: ", StringComparison.Ordinal))?.Substring(11).Trim() ?? "";
        GoalWakeup? wakeup = _goals.Wakeups.FirstOrDefault(p => p.Id == id);
        if (wakeup == null) return;
        wakeup.Status = "dispatched"; wakeup.DispatchedAtUtc = DateTime.UtcNow.ToString("O");
        _goalsDirty = true; FlushLongTermMemory();
    }

    private CommandResponse RequestGoalQuestionCommand(GameCommand command)
    {
        string goalId = ShopText(command, "goal_id");
        string prompt = ShopText(command, "question");
        LongTermGoal current = FindGoal(goalId);
        if (GoalQuestionPolicy.IsRoutineOperationalChoice(prompt) && GoalHasRoutineDecisionAuthority(current, prompt))
            return FarmReply(command, new { status = "AUTONOMOUS_DECISION_REQUIRED", goalId, questionSuppressed = true,
                note = "This is an authorized routine implementation choice. Choose seeds, quantity, ordinary crop care, shop timing, or the lowest-value safe inventory sale from live observations and continue without asking the user." });
        GoalQuestion? answered = FindAnsweredGoalQuestion(goalId, prompt);
        if (answered != null)
            return FarmReply(command, new { status = "ALREADY_ANSWERED", goalId, duplicateSuppressed = true,
                previousQuestionId = answered.Id, previousQuestion = answered.Prompt, previousAnswer = answered.Answer,
                note = "Do not ask this question again. Reuse the saved answer. If progress still needs user input, ask a materially different question about the unresolved condition." });
        LongTermGoal goal = RequestGoalQuestion(goalId, prompt,
            ReadStringList(command, "options"));
        return FarmReply(command, new
        {
            status = "USER_INPUT_REQUIRED", goalId = goal.Id, question = goal.PendingQuestion,
            note = "The in-game response window will open after the current AI run stops. End this run without guessing an answer."
        });
    }

    private static bool GoalHasRoutineDecisionAuthority(LongTermGoal goal, string prompt)
    {
        string lower = (prompt ?? "").ToLowerInvariant();
        HashSet<string> allowed = goal.Constraints.AuthorizedActions.Select(NormalizeGoalAction)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (new[] { "all", "all_actions", "autonomous_earning", "earn_money", "profit_strategy" }.Any(allowed.Contains)) return true;
        if ((lower.Contains("씨앗") || lower.Contains("seed")) && allowed.Contains("buy_seeds") && allowed.Contains("farm_crops")) return true;
        if ((lower.Contains("시든") || lower.Contains("죽은 작물") || lower.Contains("dead crop")) && allowed.Contains("farm_crops")) return true;
        if ((lower.Contains("피에르") || lower.Contains("pierre") || lower.Contains("상점") || lower.Contains("shop"))
            && (allowed.Contains("buy_seeds") || allowed.Contains("sell_crops")) && goal.Constraints.AllowDailySleep) return true;
        if ((lower.Contains("인벤토리") || lower.Contains("inventory"))
            && (allowed.Contains("manage_inventory") || allowed.Contains("sell_crops"))) return true;
        if ((lower.Contains("판매") || lower.Contains("sell"))
            && (allowed.Contains("manage_inventory") || allowed.Contains("sell_crops"))) return true;
        return false;
    }

    private CommandResponse ApplyGoalActionAuthorizationCommand(GameCommand command)
    {
        LongTermGoal goal = FindGoal(ShopText(command, "goal_id"));
        string questionId = ShopText(command, "question_id").Trim();
        GoalQuestion question = goal.QuestionHistory.FirstOrDefault(p => p.Id.Equals(questionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown goal question_id.");
        if (question.Status != "answered" || string.IsNullOrWhiteSpace(question.Answer))
            throw new InvalidOperationException("The referenced question has no saved user answer.");
        string answer = question.Answer.Trim().ToLowerInvariant();
        bool affirmative = (answer.Contains("허용") && !answer.Contains("허용하지") && !answer.Contains("계획만"))
            || answer is "yes" or "y" or "allow" or "allow all";
        if (!affirmative) throw new InvalidOperationException("The saved answer does not explicitly authorize actions.");
        HashSet<string> supported = new(StringComparer.OrdinalIgnoreCase) { "sell_crops", "buy_seeds", "tend_existing_crops", "farm_crops" };
        List<string> actions = ReadStringList(command, "actions").Select(NormalizeGoalAction).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (actions.Count == 0 || actions.Any(p => !supported.Contains(p)))
            throw new InvalidOperationException("Actions must contain only sell_crops, buy_seeds, tend_existing_crops or farm_crops.");
        if (actions.Any(p => !question.Prompt.Contains(p, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The saved question did not name every requested action.");
        foreach (string action in actions)
            if (!goal.Constraints.AuthorizedActions.Select(NormalizeGoalAction).Contains(action, StringComparer.OrdinalIgnoreCase))
                goal.Constraints.AuthorizedActions.Add(action);
        if (goal.Plan.Status != GoalPlanStatuses.None)
        {
            goal.Plan.Status = GoalPlanStatuses.Stale;
            goal.Plan.BlockedReason = "AUTHORIZATION_UPDATED";
        }
        TouchGoal(goal); _goalsDirty = true; FlushLongTermMemory();
        return FarmReply(command, new { status = "SAVED", goalId = goal.Id, authorizedActions = goal.Constraints.AuthorizedActions,
            sourceQuestionId = question.Id, sourceAnswer = question.Answer, note = "Refresh the goal plan. This command executes no gameplay action." });
    }

    private static List<string> ReadStringList(GameCommand command, string name)
    {
        if (!command.Params.TryGetValue(name, out object? raw) || raw == null) return new();
        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String)
                .Select(p => p.GetString() ?? "").Where(p => p.Trim().Length > 0).ToList();
        if (raw is IEnumerable<string> values) return values.ToList();
        string text = raw.ToString() ?? "";
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool ReadCommandBool(GameCommand command, string name)
    {
        if (!command.Params.TryGetValue(name, out object? raw) || raw == null) return false;
        if (raw is bool value) return value;
        if (raw is JsonElement element && element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        return bool.TryParse(raw.ToString(), out bool parsed) && parsed;
    }
}
