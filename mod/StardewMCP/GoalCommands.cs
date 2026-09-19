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
            note = "The goal is persistent. Phase 2 can compare profit candidates but does not autonomously select or execute them. Completion is verified from live money."
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
