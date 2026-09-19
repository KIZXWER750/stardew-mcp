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
	for _, required := range []string{"build_goal_plan", "inspect_goal_plan", "refresh_goal_plan", "Only start_goal_plan_execution", "request_goal_input", "Never treat a generated plan"} {
		if !strings.Contains(text, required) {
			t.Fatalf("missing phase-three planning contract %q", required)
		}
	}
	planner, err := os.ReadFile("../mod/StardewMCP/GoalPlanning.cs")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{"MISSING_ACTION_AUTHORIZATION", "StateFingerprint", "GoalActionAuthorized", "RefreshGoalPlanFreshness", "start_goal_plan_execution", "USER_INPUT_REQUIRED", "판매할 수확물을 준비한 뒤 재개", "existing_crop_cycle", "tend_existing_crops", "ExistingCropFingerprint"} {
		if !strings.Contains(string(planner), required) {
			t.Fatalf("missing persistent planner contract %q", required)
		}
	}
	loader, err := os.ReadFile("../mod/StardewMCP/EconomicPlanning.cs")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{"ResolveGameDataType", "AppDomain.CurrentDomain.GetAssemblies", "StardewValley.GameData"} {
		if !strings.Contains(string(loader), required) {
			t.Fatalf("missing installed game-data loader contract %q", required)
		}
	}
}
