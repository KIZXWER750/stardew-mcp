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
	for _, required := range []string{"start_goal_plan_execution", "continue_goal_plan_execution", "bind_goal_plan_plot", "report_goal_plan_step", "exact step_id and lease_id", "automatically resumes", "GOAL WAITING:", "Never call it TASK_BLOCKED"} {
		if !strings.Contains(string(tools), required) {
			t.Fatalf("missing execution tool contract %q", required)
		}
	}
	executor, err := os.ReadFile("../mod/StardewMCP/GoalExecution.cs")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{"STATE_CHANGED_BEFORE_EXECUTION", "FindLeasedStep", "VerifyCompletedPlanStep", "BeforeMoney", "DryCropTiles", "GetPendingGoalPlanExecutionPrompt", "GetPendingGoalPlanDayAdvancePrompt", "MarkGoalPlanDayAdvanceDispatched", "CYCLE_COMPLETE_REPLAN_REQUIRED", "WAITING_FOR_START_TIME", "SchedulePausedShopStep", "existingCrops", "tend_existing_crops"} {
		if !strings.Contains(string(executor), required) {
			t.Fatalf("missing execution verifier %q", required)
		}
	}
	ui, err := os.ReadFile("../mod/StardewMCP/IngameAgent.cs")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(ui), "MarkGoalPlanExecutionDispatched") || !strings.Contains(string(ui), "MarkGoalPlanDayAdvanceDispatched") {
		t.Fatal("automatic persisted plan resume is not wired")
	}
}
