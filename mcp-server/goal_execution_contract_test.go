package main

import (
	"os"
	"strings"
	"testing"
)

func TestGoalExecutionContract(t *testing.T) {
	tools, err := os.ReadFile("goal_execution_tools.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{"start_goal_plan_execution", "continue_goal_plan_execution", "bind_goal_plan_plot", "report_goal_plan_step", "exact step_id and lease_id", "automatically resumes"} {
		if !strings.Contains(string(tools), required) {
			t.Fatalf("missing execution tool contract %q", required)
		}
	}
	executor, err := os.ReadFile("../mod/StardewMCP/GoalExecution.cs")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{"STATE_CHANGED_BEFORE_EXECUTION", "FindLeasedStep", "VerifyCompletedPlanStep", "BeforeMoney", "DryCropTiles", "GetPendingGoalPlanExecutionPrompt", "CYCLE_COMPLETE_REPLAN_REQUIRED"} {
		if !strings.Contains(string(executor), required) {
			t.Fatalf("missing execution verifier %q", required)
		}
	}
	ui, err := os.ReadFile("../mod/StardewMCP/IngameAgent.cs")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(ui), "MarkGoalPlanExecutionDispatched") {
		t.Fatal("automatic persisted plan resume is not wired")
	}
}
