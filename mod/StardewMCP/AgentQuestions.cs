using System;
using System.Collections.Generic;

namespace StardewMCP;

public sealed class AgentQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public string ContinuationContext { get; set; } = "";
    public bool Presented { get; set; }
}

public partial class CommandExecutor
{
    private AgentQuestion? _pendingAgentQuestion;

    private CommandResponse RequestAgentQuestionCommand(GameCommand command)
    {
        string prompt = ShopText(command, "question").Trim();
        string context = ShopText(command, "continuation_context").Trim();
        if (prompt.Length is < 1 or > 2000) throw new InvalidOperationException("Question must be 1..2000 characters.");
        if (context.Length > 4000) throw new InvalidOperationException("Continuation context must be at most 4000 characters.");
        var options = new List<string>();
        if (command.Params.TryGetValue("options", out object? raw) && raw is System.Text.Json.JsonElement json && json.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (System.Text.Json.JsonElement item in json.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String && options.Count < 6 && !string.IsNullOrWhiteSpace(item.GetString())) options.Add(item.GetString()!.Trim());
        _pendingAgentQuestion = new AgentQuestion { Prompt = prompt, Options = options, ContinuationContext = context };
        return FarmReply(command, new { status = "USER_INPUT_REQUIRED", questionId = _pendingAgentQuestion.Id,
            note = "The dedicated in-game question window will collect the answer. Stop this run without repeating the question in chat." });
    }

    public bool TryGetPendingAgentQuestion(out AgentQuestion? question)
    {
        question = _pendingAgentQuestion;
        return question != null;
    }

    public void MarkAgentQuestionPresented(string id)
    {
        if (_pendingAgentQuestion?.Id == id) _pendingAgentQuestion.Presented = true;
    }

    public AgentQuestion AnswerAgentQuestion(string id)
    {
        if (_pendingAgentQuestion == null || _pendingAgentQuestion.Id != id) throw new InvalidOperationException("Pending question does not match.");
        AgentQuestion result = _pendingAgentQuestion;
        _pendingAgentQuestion = null;
        return result;
    }
}
