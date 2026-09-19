package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"

	copilot "github.com/github/copilot-sdk/go"
)

type testTransport func(*http.Request) (*http.Response, error)

func (f testTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func apiTestResponse(status int, body string) *http.Response {
	return &http.Response{StatusCode: status, Body: io.NopCloser(strings.NewReader(body)), Header: make(http.Header)}
}
func testAISession(t *testing.T, handler func() string) *openAISession {
	t.Helper()
	tool := copilot.DefineTool("observe", "Read state", func(p struct {
		X int `json:"x"`
	}, _ copilot.ToolInvocation) (string, error) {
		return handler(), nil
	})
	s, err := newOpenAISession(aiConfig{Provider: "openai", Model: openAIModel, key: "test-private-key"}, &copilot.SessionConfig{AvailableTools: []string{"observe"}, Tools: []copilot.Tool{tool}})
	if err != nil {
		t.Fatal(err)
	}
	return s
}

func TestAIConfigFixedMediumAndMissingKey(t *testing.T) {
	t.Setenv("STARDEW_AI_PROVIDER", "")
	t.Setenv("OPENAI_API_KEY", "")
	if _, err := loadAIConfig(); err == nil {
		t.Fatal("missing key accepted")
	}
	t.Setenv("OPENAI_API_KEY", "test-private-key")
	c, err := loadAIConfig()
	if err != nil || c.Model != openAIModel || !strings.Contains(c.description(), "medium") {
		t.Fatal(c.description(), err)
	}
	if strings.Contains(c.description(), c.key) {
		t.Fatal("key leaked")
	}
	t.Setenv("STARDEW_AI_PROVIDER", "copilot")
	t.Setenv("OPENAI_API_KEY", "")
	c, err = loadAIConfig()
	if err != nil || c.Model != "gpt-4.1" {
		t.Fatal(err)
	}
	t.Setenv("STARDEW_AI_PROVIDER", "unknown")
	if _, err = loadAIConfig(); err == nil {
		t.Fatal("invalid provider accepted")
	}
}

func TestOpenAIActualToolRegistry(t *testing.T) {
	a := &StardewAgent{}
	s, err := newOpenAISession(aiConfig{Provider: "openai", Model: openAIModel, key: "test-key"}, a.toolSessionConfig())
	if err != nil {
		t.Fatal(err)
	}
	if len(s.tools) != 90 {
		t.Fatalf("tool count = %d", len(s.tools))
	}
	if _, ok := s.tools["open_pierre_shop"]; !ok {
		t.Fatal("Pierre counter opener missing")
	}
	if _, ok := s.tools["enter_pierre_shop"]; !ok {
		t.Fatal("Pierre entrance tool missing")
	}
	for _, name := range []string{"inspect_capability_registry", "assess_economic_state", "analyze_crop_profit_options", "find_profit_opportunities", "restore_tilled_soil", "remove_dead_crops", "assess_daily_status", "find_food_options", "find_recovery_options", "consume_food", "find_home_route", "return_home", "schedule_bedtime", "sleep_until_morning", "manage_daily_life", "remove_wild_trees", "collect_loose_items", "inspect_closed_storage", "take_storage_item", "store_inventory_item", "stack_inventory_to_storage", "organize_storage", "free_inventory_slot_at_pierre", "search_memory", "get_chest_memory", "set_chest_purpose", "remember_note", "list_persistent_tasks", "upsert_persistent_task", "complete_persistent_task", "lookup_game_knowledge", "find_world_route", "create_long_term_goal", "list_long_term_goals", "inspect_long_term_goal", "verify_long_term_goal", "pause_long_term_goal", "resume_long_term_goal", "cancel_long_term_goal", "request_goal_input", "request_player_input", "set_goal_daily_life_policy", "schedule_goal_wakeup", "cancel_goal_wakeup"} {
		if _, ok := s.tools[name]; !ok {
			t.Fatal("life tool missing: " + name)
		}
	}
	for _, name := range []string{"build_goal_plan", "inspect_goal_plan", "refresh_goal_plan"} {
		if _, ok := s.tools[name]; !ok {
			t.Fatal("goal plan tool missing: " + name)
		}
	}
	for _, name := range []string{"start_goal_plan_execution", "continue_goal_plan_execution", "bind_goal_plan_plot", "report_goal_plan_step"} {
		if _, ok := s.tools[name]; !ok {
			t.Fatal("goal execution tool missing: " + name)
		}
	}
	if _, ok := s.tools["apply_goal_action_authorization"]; !ok {
		t.Fatal("goal authorization tool missing")
	}
	for name := range s.tools {
		if strings.HasPrefix(name, "cheat_") {
			t.Fatal(name)
		}
	}
	if strings.Contains(s.instructions, "## CHEAT MODE") {
		t.Fatal("cheat manual exposed")
	}
}

func TestOpenAIStartupDoesNotLaunchCopilot(t *testing.T) {
	t.Setenv("STARDEW_AI_PROVIDER", "openai")
	t.Setenv("OPENAI_API_KEY", "test-private-key")
	t.Setenv("COPILOT_CLI_PATH", "/this-cli-does-not-exist")
	a, err := NewStardewAgent()
	if err != nil || a.client != nil || a.aiConfig.Model != openAIModel {
		t.Fatal("OpenAI startup used Copilot", err)
	}
}

func TestOpenAIKeyRedaction(t *testing.T) {
	s := testAISession(t, func() string { return "echo test-private-key" })
	output, err := s.execute(context.Background(), responseItem{Name: "observe", CallID: "r", Arguments: `{"x":1}`})
	if err != nil || strings.Contains(output, "test-private-key") {
		t.Fatal("tool result leaked key")
	}
	s.client.Transport = testTransport(func(*http.Request) (*http.Response, error) {
		return apiTestResponse(200, `{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"echo test-private-key"}]}]}`), nil
	})
	result, err := s.SendAndWait(context.Background(), copilot.MessageOptions{Prompt: "test"})
	if err != nil || strings.Contains(result.Data.(*copilot.AssistantMessageData).Content, "test-private-key") {
		t.Fatal("final result leaked key")
	}
}

func TestOpenAIToolLoopAndReasoningContinuity(t *testing.T) {
	executions, requests := 0, 0
	s := testAISession(t, func() string { executions++; return "verified" })
	s.client.Transport = testTransport(func(r *http.Request) (*http.Response, error) {
		requests++
		if r.Header.Get("Authorization") != "Bearer test-private-key" {
			t.Fatal("missing authorization")
		}
		var req map[string]any
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			t.Fatal(err)
		}
		if req["model"] != openAIModel || req["reasoning"].(map[string]any)["effort"] != "medium" || req["store"] != false || req["parallel_tool_calls"] != false {
			t.Fatal("wrong request contract")
		}
		input, _ := json.Marshal(req["input"])
		if requests == 1 {
			return apiTestResponse(200, `{"status":"completed","output":[{"type":"reasoning","id":"r1","summary":[],"encrypted_content":"opaque"},{"type":"function_call","call_id":"call1","name":"observe","arguments":"{\"x\":1}"}]}`), nil
		}
		if !strings.Contains(string(input), "opaque") || !strings.Contains(string(input), "verified") || !strings.Contains(string(input), "call1") {
			t.Fatal("missing reasoning or function output history")
		}
		return apiTestResponse(200, `{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"GOAL COMPLETE"}]}]}`), nil
	})
	result, err := s.SendAndWait(context.Background(), copilot.MessageOptions{Prompt: "observe once"})
	if err != nil {
		t.Fatal(err)
	}
	if result.Data.(*copilot.AssistantMessageData).Content != "GOAL COMPLETE" || executions != 1 || requests != 2 {
		t.Fatal("loop or completion incorrect")
	}
}

func TestOpenAIToolValidationAndDedup(t *testing.T) {
	executions := 0
	s := testAISession(t, func() string { executions++; return "ok" })
	valid := responseItem{Name: "observe", CallID: "one", Arguments: `{"x":1}`}
	for i := 0; i < 2; i++ {
		if _, err := s.execute(context.Background(), valid); err != nil {
			t.Fatal(err)
		}
	}
	if executions != 1 {
		t.Fatal("duplicate action executed")
	}
	for _, call := range []responseItem{
		{Name: "cheat_warp", CallID: "bad", Arguments: `{}`},
		{Name: "observe", CallID: "bad", Arguments: `{"x":"bad"}`},
		{Name: "observe", CallID: "bad", Arguments: `{"x":1,"unexpected":true}`},
		{Name: "observe", CallID: "bad", Arguments: `null`},
		{Name: "observe", CallID: "", Arguments: `{"x":1}`},
		{Name: "observe", CallID: "one", Arguments: `{"x":2}`},
	} {
		if _, err := s.execute(context.Background(), call); err == nil {
			t.Fatalf("accepted bad call: %+v", call)
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := s.execute(ctx, responseItem{Name: "observe", CallID: "new", Arguments: `{"x":1}`}); err == nil {
		t.Fatal("cancel ignored")
	}
	if executions != 1 {
		t.Fatal("invalid action executed")
	}
}

func TestOpenAIOptionalNullIsOmitted(t *testing.T) {
	type optionalParams struct {
		X    int    `json:"x"`
		Note string `json:"note,omitempty"`
	}
	executions := 0
	tool := copilot.DefineTool("optional", "test optional fields", func(p optionalParams, _ copilot.ToolInvocation) (string, error) {
		executions++
		if p.X != 1 || p.Note != "" {
			t.Fatal("optional null was not treated as omitted")
		}
		return "ok", nil
	})
	s, err := newOpenAISession(aiConfig{Provider: "openai", Model: openAIModel, key: "test-key"}, &copilot.SessionConfig{AvailableTools: []string{"optional"}, Tools: []copilot.Tool{tool}})
	if err != nil {
		t.Fatal(err)
	}
	output, err := s.execute(context.Background(), responseItem{Name: "optional", CallID: "optional-null", Arguments: `{"x":1,"note":null}`})
	if err != nil || output != "ok" || executions != 1 {
		t.Fatalf("optional null normalization failed: output=%q err=%v executions=%d", output, err, executions)
	}
}

func TestOpenAIArgumentErrorCanBeCorrectedBeforeAction(t *testing.T) {
	executions, requests := 0, 0
	s := testAISession(t, func() string { executions++; return "verified" })
	s.client.Transport = testTransport(func(*http.Request) (*http.Response, error) {
		requests++
		switch requests {
		case 1:
			return apiTestResponse(200, `{"status":"completed","output":[{"type":"function_call","call_id":"bad","name":"observe","arguments":"{\"x\":\"wrong\"}"}]}`), nil
		case 2:
			return apiTestResponse(200, `{"status":"completed","output":[{"type":"function_call","call_id":"good","name":"observe","arguments":"{\"x\":1}"}]}`), nil
		default:
			return apiTestResponse(200, `{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"GOAL COMPLETE"}]}]}`), nil
		}
	})
	result, err := s.SendAndWait(context.Background(), copilot.MessageOptions{Prompt: "test"})
	if err != nil {
		t.Fatal(err)
	}
	if executions != 1 || requests != 3 || result.Data.(*copilot.AssistantMessageData).Content != "GOAL COMPLETE" {
		t.Fatal("unsafe correction behavior")
	}
}

func TestOpenAIReceivesLongTermEconomicExecutionRules(t *testing.T) {
	s := testAISession(t, func() string { return "ok" })
	for _, required := range []string{
		"LONG-TERM GOALS:", "ECONOMIC PLANNING:", "PERSISTENT GOAL PLAN EXECUTION (PHASE 4):",
		"For every newly created broad money goal", "start_goal_plan_execution", "GOAL WAITING:",
	} {
		if !strings.Contains(s.instructions, required) {
			t.Fatalf("OpenAI instructions missing %q", required)
		}
	}
}

func TestOpenAIOversizedToolResultReturnsRecoverableObservation(t *testing.T) {
	executions := 0
	tool := copilot.DefineTool("large_read", "returns a large observation", func(p struct {
		X int `json:"x"`
	}, _ copilot.ToolInvocation) (string, error) {
		executions++
		return strings.Repeat("x", 128*1024+1), nil
	})
	s, err := newOpenAISession(aiConfig{Provider: "openai", Model: openAIModel, key: "test-key"}, &copilot.SessionConfig{AvailableTools: []string{"large_read"}, Tools: []copilot.Tool{tool}})
	if err != nil {
		t.Fatal(err)
	}
	output, err := s.execute(context.Background(), responseItem{Name: "large_read", CallID: "large-1", Arguments: `{"x":1}`})
	if err != nil || executions != 1 || !strings.Contains(output, `"status":"TOOL_RESULT_TOO_LARGE"`) || !strings.Contains(output, `"actionMayHaveExecuted":true`) {
		t.Fatalf("oversized result was not recoverable: output=%q err=%v executions=%d", output, err, executions)
	}
}

func TestOpenAIHTTPFailuresDoNotRetryOrLeak(t *testing.T) {
	for _, status := range []int{301, 400, 401, 403, 404, 429, 500, 503} {
		t.Run(fmt.Sprint(status), func(t *testing.T) {
			s := testAISession(t, func() string { t.Fatal("tool ran"); return "" })
			requests := 0
			s.client.Transport = testTransport(func(*http.Request) (*http.Response, error) {
				requests++
				return apiTestResponse(status, `test-private-key sensitive error`), nil
			})
			_, err := s.SendAndWait(context.Background(), copilot.MessageOptions{Prompt: "test"})
			if err == nil || strings.Contains(err.Error(), "test-private-key") || requests != 1 {
				t.Fatal("unsafe HTTP handling", err)
			}
		})
	}
}

func TestOpenAIRejectIncompleteParallelAndMalformed(t *testing.T) {
	for _, body := range []string{
		`not json`,
		`{"status":"incomplete","output":[{"type":"function_call","call_id":"one","name":"observe","arguments":"{\"x\":1}"}]}`,
		`{"status":"completed","output":[{"type":"function_call","call_id":"one","name":"observe","arguments":"{\"x\":1}"},{"type":"function_call","call_id":"two","name":"observe","arguments":"{\"x\":2}"}]}`,
		`{"status":"completed","output":[]}`,
	} {
		s := testAISession(t, func() string { t.Fatal("unsafe action"); return "" })
		s.client.Transport = testTransport(func(*http.Request) (*http.Response, error) { return apiTestResponse(200, body), nil })
		if _, err := s.SendAndWait(context.Background(), copilot.MessageOptions{Prompt: "test"}); err == nil {
			t.Fatal("invalid response accepted")
		}
	}
}

func TestOpenAIRoundLimit(t *testing.T) {
	calls := 0
	s := testAISession(t, func() string { return "ok" })
	s.client.Transport = testTransport(func(*http.Request) (*http.Response, error) {
		calls++
		return apiTestResponse(200, fmt.Sprintf(`{"status":"completed","output":[{"type":"function_call","call_id":"c%d","name":"observe","arguments":"{\"x\":1}"}]}`, calls)), nil
	})
	_, err := s.SendAndWait(context.Background(), copilot.MessageOptions{Prompt: "test"})
	if err == nil || !strings.Contains(err.Error(), "ROUND_LIMIT") || calls != maxAIToolRounds {
		t.Fatal(err, calls)
	}
}
