using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    public IReadOnlyList<LongTermGoal> GetLongTermGoals(string? status = null)
    {
        if (!_memoryLoaded) return Array.Empty<LongTermGoal>();
        RefreshLongTermGoalProgress();
        IEnumerable<LongTermGoal> selected = string.IsNullOrWhiteSpace(status) ? _goals.Goals
            : _goals.Goals.Where(p => p.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        return selected.Select(Clone).ToList();
    }

    public LongTermGoal CreateMoneyGoal(string summary, string metric, int targetValue, int reserveMoney,
        int latestWorkTime, string strategyPreference, IEnumerable<string>? authorizedActions,
        IEnumerable<string>? preserveItemIds, int? deadlineDayIndex, string deadlineLabel)
    {
        if (!_memoryLoaded) throw new InvalidOperationException("Long-term memory is not loaded.");
        if (string.IsNullOrWhiteSpace(summary) || summary.Trim().Length > 1000)
            throw new InvalidOperationException("Goal summary must be 1..1000 characters.");
        if (targetValue <= 0 || targetValue > 1_000_000_000) throw new InvalidOperationException("Money target must be 1..1000000000.");
        if (reserveMoney < 0 || reserveMoney > targetValue) throw new InvalidOperationException("Reserve money must be between zero and the target value.");
        if (latestWorkTime < 600 || latestWorkTime > 2600) throw new InvalidOperationException("Latest work time must be 600..2600.");
        metric = GoalSchema.NormalizeMoneyMetric(metric);
        if (_goals.Goals.Any(p => !GoalStatuses.IsTerminal(p.Status) && p.Status != GoalStatuses.Paused && p.Status != GoalStatuses.Draft))
            throw new InvalidOperationException("Another long-term goal is already active or waiting for input. Pause or finish it first.");
        string now = DateTime.UtcNow.ToString("O");
        var goal = GoalSchema.Normalize(new LongTermGoal
        {
            Kind = GoalKinds.MoneyTarget, Summary = summary.Trim(), Status = GoalStatuses.Active,
            Money = new MoneyGoalSpec
            {
                Metric = metric, TargetValue = targetValue, StartingMoney = Game1.player.Money,
                DeadlineDayIndex = deadlineDayIndex, DeadlineLabel = deadlineLabel?.Trim() ?? ""
            },
            Constraints = new GoalConstraints
            {
                ReserveMoney = reserveMoney, LatestWorkTime = latestWorkTime,
                StrategyPreference = string.IsNullOrWhiteSpace(strategyPreference) ? "balanced" : strategyPreference.Trim(),
                AuthorizedActions = NormalizeStringList(authorizedActions), PreserveItemIds = NormalizeStringList(preserveItemIds)
            },
            CreatedGameDate = FarmDate(), CreatedGameTime = Game1.timeOfDay,
            UpdatedGameDate = FarmDate(), UpdatedGameTime = Game1.timeOfDay,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        goal.Progress = GoalSchema.EvaluateMoney(goal, Game1.player.Money, CurrentDayIndex(), FarmDate(), Game1.timeOfDay);
        if (goal.Progress.SuccessConditionMet) goal.Status = GoalStatuses.Completed;
        _goals.Goals.Add(goal);
        _goalsDirty = true;
        FlushLongTermMemory();
        return Clone(goal);
    }

    public LongTermGoal TransitionLongTermGoal(string id, string status, string reason = "")
    {
        LongTermGoal goal = FindGoal(id);
        status = GoalSchema.NormalizeStatus(status);
        if (!GoalSchema.CanTransition(goal.Status, status))
            throw new InvalidOperationException($"Goal status cannot transition from {goal.Status} to {status}.");
        goal.Status = status;
        goal.BlockedReason = status == GoalStatuses.Blocked ? reason.Trim() : "";
        if (status != GoalStatuses.AwaitingUser && goal.PendingQuestion?.Status == "pending")
        {
            goal.PendingQuestion.Status = "dismissed";
            SyncQuestionHistory(goal, goal.PendingQuestion);
            goal.PendingQuestion = null;
        }
        TouchGoal(goal);
        _goalsDirty = true;
        FlushLongTermMemory();
        return Clone(goal);
    }

    public LongTermGoal RequestGoalQuestion(string goalId, string prompt, IEnumerable<string>? options)
    {
        LongTermGoal goal = FindGoal(goalId);
        if (GoalStatuses.IsTerminal(goal.Status)) throw new InvalidOperationException("A terminal goal cannot request user input.");
        prompt = prompt?.Trim() ?? "";
        if (prompt.Length is < 1 or > 1000) throw new InvalidOperationException("Question must be 1..1000 characters.");
        if (goal.PendingQuestion?.Status == "pending") throw new InvalidOperationException("This goal already has a pending question.");
        var question = new GoalQuestion
        {
            Prompt = prompt, Options = NormalizeStringList(options).Take(6).ToList(),
            CreatedAtUtc = DateTime.UtcNow.ToString("O")
        };
        goal.PendingQuestion = question;
        goal.QuestionHistory.Add(question);
        goal.Status = GoalStatuses.AwaitingUser;
        goal.Progress.Summary = "사용자 응답 대기: " + prompt;
        TouchGoal(goal);
        _goalsDirty = true;
        FlushLongTermMemory();
        return Clone(goal);
    }

    public LongTermGoal AnswerGoalQuestion(string goalId, string questionId, string answer)
    {
        LongTermGoal goal = FindGoal(goalId);
        GoalQuestion question = goal.PendingQuestion ?? throw new InvalidOperationException("This goal has no pending question.");
        if (!question.Id.Equals(questionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending question ID does not match.");
        answer = answer?.Trim() ?? "";
        if (answer.Length is < 1 or > 2000) throw new InvalidOperationException("Answer must be 1..2000 characters.");
        question.Answer = answer;
        question.AnsweredAtUtc = DateTime.UtcNow.ToString("O");
        question.Status = "answered";
        SyncQuestionHistory(goal, question);
        goal.PendingQuestion = null;
        goal.Status = GoalStatuses.Active;
        goal.Progress.Summary = "사용자 응답을 반영해 목표 재개 대기";
        TouchGoal(goal);
        _goalsDirty = true;
        FlushLongTermMemory();
        return Clone(goal);
    }

    public bool TryGetPendingGoalQuestion(out string goalId, out GoalQuestion? question)
    {
        LongTermGoal? goal = _goals.Goals.FirstOrDefault(p => p.Status == GoalStatuses.AwaitingUser && p.PendingQuestion?.Status == "pending");
        goalId = goal?.Id ?? "";
        question = goal?.PendingQuestion == null ? null : Clone(goal.PendingQuestion);
        return question != null;
    }

    public void MarkGoalQuestionPresented(string goalId, string questionId)
    {
        LongTermGoal goal = FindGoal(goalId);
        if (goal.PendingQuestion == null || !goal.PendingQuestion.Id.Equals(questionId, StringComparison.OrdinalIgnoreCase)) return;
        if (goal.PendingQuestion.Presented) return;
        goal.PendingQuestion.Presented = true;
        SyncQuestionHistory(goal, goal.PendingQuestion);
        _goalsDirty = true;
        FlushLongTermMemory();
    }

    public void RefreshLongTermGoalProgress()
    {
        if (!_memoryLoaded || !Context.IsWorldReady) return;
        bool changed = false;
        foreach (LongTermGoal goal in _goals.Goals.Where(p => !GoalStatuses.IsTerminal(p.Status)))
        {
            bool goalChanged = false;
            GoalProgress next = GoalSchema.EvaluateMoney(goal, Game1.player.Money, CurrentDayIndex(), FarmDate(), Game1.timeOfDay);
            if (!SameProgress(goal.Progress, next)) { goal.Progress = next; goalChanged = true; }
            if (next.SuccessConditionMet)
            {
                goal.Status = GoalStatuses.Completed;
                goal.PendingQuestion = null;
                goal.BlockedReason = "";
                goalChanged = true;
            }
            else if (next.DeadlineMissed && goal.Status == GoalStatuses.Active)
            {
                goal.Status = GoalStatuses.Blocked;
                goal.BlockedReason = "DEADLINE_MISSED";
                goalChanged = true;
            }
            if (goalChanged) { TouchGoal(goal); changed = true; }
        }
        if (!changed) return;
        _goalsDirty = true;
        FlushLongTermMemory();
    }

    public string GetLongTermGoalHudText()
    {
        if (!_memoryLoaded || !Context.IsWorldReady) return "";
        LongTermGoal? goal = _goals.Goals.Where(p => !GoalStatuses.IsTerminal(p.Status))
            .OrderByDescending(p => p.Status == GoalStatuses.Active || p.Status == GoalStatuses.AwaitingUser).ThenByDescending(p => p.UpdatedAtUtc).FirstOrDefault();
        if (goal == null) return "";
        string state = goal.Status == GoalStatuses.AwaitingUser ? "응답 대기" : goal.Status;
        string plan = goal.Plan.Status == GoalPlanStatuses.Ready
            ? $"\n계획 · {goal.Plan.StrategyTitle}"
            : goal.Plan.Status == GoalPlanStatuses.Stale ? "\n계획 · 재계획 필요" : "";
        return $"장기 목표 · {state}\n{goal.Summary}\n{goal.Progress.CurrentValue:N0} / {goal.Progress.TargetValue:N0}g ({goal.Progress.Percent:0.#}%){plan}";
    }

    public void PauseActiveLongTermGoalsByUser()
    {
        if (!_memoryLoaded) return;
        bool changed = false;
        foreach (LongTermGoal goal in _goals.Goals.Where(p => p.Status is GoalStatuses.Active or GoalStatuses.AwaitingUser))
        {
            goal.Status = GoalStatuses.Paused;
            goal.BlockedReason = "USER_CANCELLED_AUTOMATION";
            if (goal.PendingQuestion?.Status == "pending")
            {
                goal.PendingQuestion.Status = "dismissed";
                SyncQuestionHistory(goal, goal.PendingQuestion);
            }
            goal.PendingQuestion = null;
            TouchGoal(goal);
            changed = true;
        }
        if (!changed) return;
        _goalsDirty = true;
        FlushLongTermMemory();
    }

    private LongTermGoal FindGoal(string id) => _goals.Goals.FirstOrDefault(p => p.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("Unknown long-term goal_id.");

    private void TouchGoal(LongTermGoal goal)
    {
        goal.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
        if (Context.IsWorldReady) { goal.UpdatedGameDate = FarmDate(); goal.UpdatedGameTime = Game1.timeOfDay; }
    }

    private static void SyncQuestionHistory(LongTermGoal goal, GoalQuestion question)
    {
        int index = goal.QuestionHistory.FindIndex(p => p.Id.Equals(question.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) goal.QuestionHistory[index] = Clone(question);
        else goal.QuestionHistory.Add(Clone(question));
    }

    private static bool SameProgress(GoalProgress a, GoalProgress b) => a.CurrentValue == b.CurrentValue
        && a.TargetValue == b.TargetValue && a.RemainingValue == b.RemainingValue
        && a.SuccessConditionMet == b.SuccessConditionMet && a.DeadlineMissed == b.DeadlineMissed
        && a.EvaluatedDayIndex == b.EvaluatedDayIndex;

    private static int CurrentDayIndex() => (int)Game1.stats.DaysPlayed;
    private static List<string> NormalizeStringList(IEnumerable<string>? values) => (values ?? Array.Empty<string>())
        .Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
}
