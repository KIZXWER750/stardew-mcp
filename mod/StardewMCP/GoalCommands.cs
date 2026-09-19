using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
            ReadStringList(command, "preserve_item_ids"), deadline, ShopText(command, "deadline_label"));
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

    private CommandResponse RequestGoalQuestionCommand(GameCommand command)
    {
        LongTermGoal goal = RequestGoalQuestion(ShopText(command, "goal_id"), ShopText(command, "question"),
            ReadStringList(command, "options"));
        return FarmReply(command, new
        {
            status = "USER_INPUT_REQUIRED", goalId = goal.Id, question = goal.PendingQuestion,
            note = "The in-game response window will open after the current AI run stops. End this run without guessing an answer."
        });
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
        HashSet<string> supported = new(StringComparer.OrdinalIgnoreCase) { "sell_crops", "buy_seeds", "farm_crops" };
        List<string> actions = ReadStringList(command, "actions").Select(NormalizeGoalAction).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (actions.Count == 0 || actions.Any(p => !supported.Contains(p)))
            throw new InvalidOperationException("Actions must contain only sell_crops, buy_seeds or farm_crops.");
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
}
