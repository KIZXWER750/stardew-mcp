package main

import (
	"os"
	"strings"
	"testing"
)

func TestLongTermGoalContract(t *testing.T) {
	tools, err := os.ReadFile("goal_tools.go")
	if err != nil {
		t.Fatal(err)
	}
	text := string(tools)
	for _, required := range []string{"create_long_term_goal", "verify_long_term_goal", "request_goal_input", "apply_goal_action_authorization", "set_goal_daily_life_policy", "schedule_goal_wakeup", "cancel_goal_wakeup", "AUTONOMOUS_DECISION_REQUIRED", "allow_daily_sleep", "leased, verified steps", "dedicated response window", "tend_existing_crops", "already-planted crops", "ALREADY_ANSWERED", "Never repeat an answered question"} {
		if !strings.Contains(text, required) {
			t.Fatalf("missing goal contract %q", required)
		}
	}
	ui, err := os.ReadFile("../mod/StardewMCP/GoalQuestionMenu.cs")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(ui), "답변하고 목표 계속") {
		t.Fatal("dedicated goal question UI is not wired")
	}
}
