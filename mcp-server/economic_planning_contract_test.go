package main

import (
	"os"
	"strings"
	"testing"
)

func TestPhaseThreePlanningContract(t *testing.T) {
	tools, err := os.ReadFile("economic_tools.go")
	if err != nil {
		t.Fatal(err)
	}
	text := string(tools)
	for _, required := range []string{"build_goal_plan", "inspect_goal_plan", "refresh_goal_plan", "never executes a plan step", "request_goal_input", "Never treat a generated plan"} {
		if !strings.Contains(text, required) {
			t.Fatalf("missing phase-three planning contract %q", required)
		}
	}
	planner, err := os.ReadFile("../mod/StardewMCP/GoalPlanning.cs")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{"MISSING_ACTION_AUTHORIZATION", "StateFingerprint", "GoalActionAuthorized", "RefreshGoalPlanFreshness", "Phase 3 does not execute steps"} {
		if !strings.Contains(string(planner), required) {
			t.Fatalf("missing persistent planner contract %q", required)
		}
	}
}
