using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewMCP;

public sealed class GoalDocument : MemoryDocument
{
    public List<LongTermGoal> Goals { get; set; } = new();
}

public sealed class LongTermGoal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = GoalKinds.MoneyTarget;
    public string Summary { get; set; } = "";
    public string Status { get; set; } = GoalStatuses.Draft;
    public MoneyGoalSpec Money { get; set; } = new();
    public GoalConstraints Constraints { get; set; } = new();
    public GoalProgress Progress { get; set; } = new();
    public GoalExecutionPlan Plan { get; set; } = new();
    public GoalQuestion? PendingQuestion { get; set; }
    public List<GoalQuestion> QuestionHistory { get; set; } = new();
    public string BlockedReason { get; set; } = "";
    public string CreatedGameDate { get; set; } = "";
    public int CreatedGameTime { get; set; }
    public string UpdatedGameDate { get; set; } = "";
    public int UpdatedGameTime { get; set; }
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
}

public sealed class MoneyGoalSpec
{
    public string Metric { get; set; } = MoneyGoalMetrics.CurrentBalance;
    public int TargetValue { get; set; }
    public int StartingMoney { get; set; }
    public int? DeadlineDayIndex { get; set; }
    public string DeadlineLabel { get; set; } = "";
}

public sealed class GoalConstraints
{
    public int ReserveMoney { get; set; }
    public int LatestWorkTime { get; set; } = 2200;
    public string StrategyPreference { get; set; } = "balanced";
    public List<string> AuthorizedActions { get; set; } = new();
    public List<string> PreserveItemIds { get; set; } = new();
}

public sealed class GoalProgress
{
    public int CurrentValue { get; set; }
    public int TargetValue { get; set; }
    public int RemainingValue { get; set; }
    public double Percent { get; set; }
    public bool SuccessConditionMet { get; set; }
    public bool DeadlineMissed { get; set; }
    public int EvaluatedDayIndex { get; set; }
    public string EvaluatedGameDate { get; set; } = "";
    public int EvaluatedGameTime { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class GoalQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public string Answer { get; set; } = "";
    public string Status { get; set; } = "pending";
    public bool Presented { get; set; }
    public string CreatedAtUtc { get; set; } = "";
    public string AnsweredAtUtc { get; set; } = "";
}

public sealed class GoalExecutionPlan
{
    public int Revision { get; set; }
    public string Status { get; set; } = GoalPlanStatuses.None;
    public string StrategyId { get; set; } = "";
    public string StrategyKind { get; set; } = "";
    public string StrategyTitle { get; set; } = "";
    public string Preference { get; set; } = "balanced";
    public int ExpectedGold { get; set; }
    public int ExpectedProfit { get; set; }
    public int UpfrontCost { get; set; }
    public int ExpectedDays { get; set; }
    public int MaxTiles { get; set; } = 16;
    public int LatestWorkTime { get; set; } = 2200;
    public int RemainingGoldAtPlanning { get; set; }
    public int PlannedDayIndex { get; set; }
    public string PlannedGameDate { get; set; } = "";
    public int PlannedGameTime { get; set; }
    public string StateFingerprint { get; set; } = "";
    public string GeneratedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
    public string BlockedReason { get; set; } = "";
    public string ExecutionStartedAtUtc { get; set; } = "";
    public string LastExecutionAtUtc { get; set; } = "";
    public int LastDispatchDayIndex { get; set; } = -1;
    public string LastDispatchStepId { get; set; } = "";
    public GoalPlanPlot? Plot { get; set; }
    public List<string> MissingAuthorizations { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
    public List<GoalPlanStep> Steps { get; set; } = new();
}

public sealed class GoalPlanStep
{
    public string Id { get; set; } = "";
    public int Sequence { get; set; }
    public int DayIndex { get; set; }
    public string DayLabel { get; set; } = "";
    public string Action { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "pending";
    public bool Conditional { get; set; }
    public bool RequiresLivePreflight { get; set; } = true;
    public List<string> DependsOn { get; set; } = new();
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int AttemptCount { get; set; }
    public string LeaseId { get; set; } = "";
    public string ClaimedAtUtc { get; set; } = "";
    public string CompletedAtUtc { get; set; } = "";
    public string ResultSummary { get; set; } = "";
    public string BlockedReason { get; set; } = "";
    public int BeforeMoney { get; set; }
    public int BeforeSeedQuantity { get; set; }
    public int BeforeHarvestQuantity { get; set; }
    public int BeforePreparedTiles { get; set; }
    public int BeforeCropTiles { get; set; }
    public int BeforeReadyCropTiles { get; set; }
    public int BeforeDryCropTiles { get; set; }
    public int BeforeSellableCropQuantity { get; set; }
    public int NotBeforeTime { get; set; }
}

public sealed class GoalPlanPlot
{
    public string Location { get; set; } = "Farm";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string BoundAtUtc { get; set; } = "";
}

public sealed class GoalPlanCandidate
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public int ExpectedGold { get; set; }
    public int ExpectedProfit { get; set; }
    public int UpfrontCost { get; set; }
    public int Days { get; set; }
    public int WorkUnits { get; set; }
    public string Confidence { get; set; } = "medium";
    public List<string> RequiredActions { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class GoalPlanStatuses
{
    public const string None = "none";
    public const string Ready = "ready";
    public const string Blocked = "blocked";
    public const string Stale = "stale";
    public const string Executing = "executing";
    public const string Waiting = "waiting";
    public const string Paused = "paused";
    public const string Completed = "completed";
}

public static class GoalPlanStepStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public const string Blocked = "blocked";
}

public static class GoalPlanPolicy
{
    public const int MaxPlanSteps = 64;

    public static int NormalizeGrowthDays(int value) => Math.Clamp(value, 0, 28);

    public static bool HasSafeShape(GoalExecutionPlan? plan) => plan != null
        && plan.Steps != null && plan.Steps.Count <= MaxPlanSteps;

    public static GoalPlanCandidate? Select(IEnumerable<GoalPlanCandidate> values, string preference, int remainingGold)
    {
        var candidates = values.Where(p => p.ExpectedGold > 0 && p.ExpectedProfit >= 0).ToList();
        if (candidates.Count == 0) return null;
        preference = (preference ?? "balanced").Trim().ToLowerInvariant();
        IOrderedEnumerable<GoalPlanCandidate> ranked = preference switch
        {
            "fastest" => candidates.OrderBy(p => p.Days).ThenByDescending(p => p.ExpectedProfit),
            "highest_profit" => candidates.OrderByDescending(p => p.ExpectedProfit).ThenBy(p => p.Days),
            "low_risk" => candidates.OrderBy(p => ConfidenceRank(p.Confidence)).ThenBy(p => p.UpfrontCost).ThenBy(p => p.Days),
            "low_effort" => candidates.OrderByDescending(Efficiency).ThenBy(p => p.Days),
            _ => candidates.OrderByDescending(p => p.ExpectedProfit >= remainingGold)
                .ThenBy(p => p.Days).ThenByDescending(Efficiency).ThenByDescending(p => p.ExpectedProfit)
        };
        return ranked.ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).First();
    }

    private static int ConfidenceRank(string value) => value.Equals("high", StringComparison.OrdinalIgnoreCase) ? 0
        : value.Equals("medium", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    private static double Efficiency(GoalPlanCandidate value) => value.ExpectedProfit / (double)Math.Max(1, value.WorkUnits);

    public static (int Width, int Height) RectangleForTiles(int tiles)
    {
        tiles = Math.Clamp(tiles, 1, 64);
        int width = Math.Min(8, (int)Math.Ceiling(Math.Sqrt(tiles)));
        while (width > 1 && tiles % width != 0) width--;
        return (width, (int)Math.Ceiling(tiles / (double)width));
    }
}

public static class GoalKinds
{
    public const string MoneyTarget = "money_target";
}

public static class MoneyGoalMetrics
{
    public const string CurrentBalance = "current_balance";
    public const string BalanceIncrease = "balance_increase";
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        { CurrentBalance, BalanceIncrease };
}

public static class GoalStatuses
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string AwaitingUser = "awaiting_user";
    public const string Paused = "paused";
    public const string Blocked = "blocked";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        { Draft, Active, AwaitingUser, Paused, Blocked, Completed, Cancelled, Failed };

    public static bool IsTerminal(string status) => status.Equals(Completed, StringComparison.OrdinalIgnoreCase)
        || status.Equals(Cancelled, StringComparison.OrdinalIgnoreCase)
        || status.Equals(Failed, StringComparison.OrdinalIgnoreCase);
}

public static class GoalSchema
{
    public const int CurrentVersion = 3;

    public static GoalDocument Normalize(GoalDocument? document)
    {
        document ??= new();
        int sourceVersion = document.SchemaVersion;
        if (document.SchemaVersion < 0 || document.SchemaVersion > CurrentVersion)
            throw new InvalidOperationException($"Unsupported goal schema version {document.SchemaVersion}.");
        if (document.SchemaVersion < 3) document.SchemaVersion = 3;
        document.Goals ??= new();
        document.Goals = document.Goals.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(p => Normalize(p.Last())).ToList();
        if (sourceVersion is 1 or 2)
            foreach (LongTermGoal goal in document.Goals.Where(p => p.Plan.Status is not (GoalPlanStatuses.None or GoalPlanStatuses.Completed)))
            {
                goal.Plan.Status = GoalPlanStatuses.Stale;
                goal.Plan.BlockedReason = "MIGRATED_REPLAN_REQUIRED";
                foreach (GoalPlanStep step in goal.Plan.Steps.Where(p => p.Status == GoalPlanStepStatuses.InProgress))
                { step.Status = GoalPlanStepStatuses.Pending; step.LeaseId = ""; }
            }
        return document;
    }

    public static LongTermGoal Normalize(LongTermGoal goal)
    {
        goal.Kind = (goal.Kind ?? "").Trim().ToLowerInvariant();
        if (goal.Kind != GoalKinds.MoneyTarget) throw new InvalidOperationException($"Unsupported goal kind '{goal.Kind}'.");
        goal.Status = NormalizeStatus(goal.Status);
        goal.Money ??= new();
        goal.Money.Metric = NormalizeMoneyMetric(goal.Money.Metric);
        goal.Constraints ??= new();
        goal.Constraints.StrategyPreference = string.IsNullOrWhiteSpace(goal.Constraints.StrategyPreference)
            ? "balanced" : goal.Constraints.StrategyPreference.Trim().ToLowerInvariant();
        goal.Constraints.AuthorizedActions ??= new();
        goal.Constraints.PreserveItemIds ??= new();
        goal.Progress ??= new();
        goal.Plan ??= new();
        goal.Plan.Status = NormalizePlanStatus(goal.Plan.Status);
        goal.Plan.Preference = string.IsNullOrWhiteSpace(goal.Plan.Preference) ? goal.Constraints.StrategyPreference : goal.Plan.Preference.Trim().ToLowerInvariant();
        goal.Plan.MissingAuthorizations ??= new();
        goal.Plan.Assumptions ??= new();
        goal.Plan.Steps ??= new();
        foreach (GoalPlanStep step in goal.Plan.Steps)
        {
            step.DependsOn ??= new();
            step.Inputs ??= new(StringComparer.OrdinalIgnoreCase);
            step.Status = NormalizePlanStepStatus(step.Status);
        }
        goal.QuestionHistory ??= new();
        if (goal.PendingQuestion != null)
        {
            goal.PendingQuestion.Options ??= new();
            if (!goal.QuestionHistory.Any(p => p.Id.Equals(goal.PendingQuestion.Id, StringComparison.OrdinalIgnoreCase)))
                goal.QuestionHistory.Add(goal.PendingQuestion);
        }
        foreach (GoalQuestion question in goal.QuestionHistory) question.Options ??= new();
        return goal;
    }

    public static string NormalizeStatus(string? status)
    {
        string value = (status ?? GoalStatuses.Draft).Trim().ToLowerInvariant();
        if (!GoalStatuses.All.Contains(value)) throw new InvalidOperationException($"Unsupported goal status '{status}'.");
        return value;
    }

    public static string NormalizeMoneyMetric(string? metric)
    {
        string value = (metric ?? MoneyGoalMetrics.CurrentBalance).Trim().ToLowerInvariant();
        if (!MoneyGoalMetrics.All.Contains(value)) throw new InvalidOperationException($"Unsupported money goal metric '{metric}'.");
        return value;
    }

    public static string NormalizePlanStatus(string? status)
    {
        string value = string.IsNullOrWhiteSpace(status) ? GoalPlanStatuses.None : status.Trim().ToLowerInvariant();
        if (value is not (GoalPlanStatuses.None or GoalPlanStatuses.Ready or GoalPlanStatuses.Blocked or GoalPlanStatuses.Stale
            or GoalPlanStatuses.Executing or GoalPlanStatuses.Waiting or GoalPlanStatuses.Paused or GoalPlanStatuses.Completed))
            throw new InvalidOperationException($"Unsupported goal plan status '{status}'.");
        return value;
    }

    public static string NormalizePlanStepStatus(string? status)
    {
        string value = string.IsNullOrWhiteSpace(status) ? GoalPlanStepStatuses.Pending : status.Trim().ToLowerInvariant();
        if (value is not (GoalPlanStepStatuses.Pending or GoalPlanStepStatuses.InProgress or GoalPlanStepStatuses.Completed
            or GoalPlanStepStatuses.Skipped or GoalPlanStepStatuses.Blocked))
            throw new InvalidOperationException($"Unsupported goal plan step status '{status}'.");
        return value;
    }

    public static bool CanTransition(string from, string to)
    {
        from = NormalizeStatus(from);
        to = NormalizeStatus(to);
        if (from == to) return true;
        if (GoalStatuses.IsTerminal(from)) return false;
        return to switch
        {
            GoalStatuses.Active => from is GoalStatuses.Draft or GoalStatuses.Paused or GoalStatuses.Blocked or GoalStatuses.AwaitingUser,
            GoalStatuses.AwaitingUser => from is GoalStatuses.Draft or GoalStatuses.Active or GoalStatuses.Blocked,
            GoalStatuses.Paused => from is GoalStatuses.Draft or GoalStatuses.Active or GoalStatuses.Blocked or GoalStatuses.AwaitingUser,
            GoalStatuses.Blocked => from is GoalStatuses.Draft or GoalStatuses.Active or GoalStatuses.Paused or GoalStatuses.AwaitingUser,
            GoalStatuses.Completed or GoalStatuses.Cancelled or GoalStatuses.Failed => true,
            _ => false
        };
    }

    public static GoalProgress EvaluateMoney(LongTermGoal goal, int currentMoney, int dayIndex, string gameDate, int gameTime)
    {
        goal = Normalize(goal);
        if (goal.Money.TargetValue <= 0) throw new InvalidOperationException("Money target must be greater than zero.");
        int value = goal.Money.Metric == MoneyGoalMetrics.BalanceIncrease
            ? Math.Max(0, currentMoney - goal.Money.StartingMoney) : currentMoney;
        int remaining = Math.Max(0, goal.Money.TargetValue - value);
        bool deadlineMissed = goal.Money.DeadlineDayIndex.HasValue && dayIndex > goal.Money.DeadlineDayIndex.Value && remaining > 0;
        return new GoalProgress
        {
            CurrentValue = value, TargetValue = goal.Money.TargetValue, RemainingValue = remaining,
            Percent = Math.Clamp(value * 100d / goal.Money.TargetValue, 0, 100), SuccessConditionMet = remaining == 0,
            DeadlineMissed = deadlineMissed, EvaluatedDayIndex = dayIndex, EvaluatedGameDate = gameDate,
            EvaluatedGameTime = gameTime,
            Summary = remaining == 0 ? $"목표 달성: {value:N0}/{goal.Money.TargetValue:N0}g"
                : deadlineMissed ? $"마감일 경과: {value:N0}/{goal.Money.TargetValue:N0}g"
                : $"진행 중: {value:N0}/{goal.Money.TargetValue:N0}g, {remaining:N0}g 남음"
        };
    }
}
