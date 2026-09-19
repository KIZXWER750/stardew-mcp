package main

import (
	"os"
	"strings"
	"testing"
)

func TestDeadCropRemovalContract(t *testing.T) {
	workBytes, err := os.ReadFile("../mod/StardewMCP/FarmWork.cs")
	if err != nil {
		t.Fatal(err)
	}
	work := string(workBytes)
	for _, required := range []string{
		`"remove_dead_crops" => !t.Dead`,
		`DEAD_CROP_REQUIRES_REMOVAL`,
		`j.Operation=="remove_dead_crops"?"Scythe"`,
		`j.Operation=="remove_dead_crops"`,
	} {
		if !strings.Contains(work, required) {
			t.Fatalf("dead-crop execution contract missing %q", required)
		}
	}

	rulesBytes, err := os.ReadFile("farm_tools.go")
	if err != nil {
		t.Fatal(err)
	}
	rules := string(rulesBytes)
	for _, required := range []string{"dead=true", "remove_dead_crops first", "before planting or watering"} {
		if !strings.Contains(rules, required) {
			t.Fatalf("dead-crop agent rule missing %q", required)
		}
	}
}

func TestCropPlanSchedulesDeadCropCleanup(t *testing.T) {
	plannerBytes, err := os.ReadFile("../mod/StardewMCP/GoalPlanning.cs")
	if err != nil {
		t.Fatal(err)
	}
	planner := string(plannerBytes)
	cleanup := strings.Index(planner, `Add(startOffset, "remove_dead_crops"`)
	plant := strings.Index(planner, `Add(startOffset, "plant_plot"`)
	if cleanup < 0 || plant < 0 || cleanup > plant {
		t.Fatal("crop plan must schedule dead-crop cleanup before planting")
	}
}
