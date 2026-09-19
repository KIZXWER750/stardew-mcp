package main

import (
	"os"
	"strings"
	"testing"
)

func TestClearingMoveParameters(t *testing.T) {
	p := ClearingMoveParams{Location: "Farm", X: 40, Y: 20}
	plot := p.plot()
	if plot.Width != 1 || plot.Height != 1 || plot.X != 40 || plot.Y != 20 {
		t.Fatal("clearing movement must map to one exact destination tile")
	}
	values := plot.values("travel")
	if values["max_obstacles"] != 8 || values["operation"] != "travel" {
		t.Fatalf("unexpected travel defaults: %#v", values)
	}
	q := plot
	q.MaxObstacles = 9
	if farmKey("travel", plot) == farmKey("travel", q) {
		t.Fatal("max_obstacles must participate in idempotency")
	}
}

func TestClearingMoveGameContractIsPackaged(t *testing.T) {
	travel, err := os.ReadFile("../mod/StardewMCP/FarmTravel.cs")
	if err != nil {
		t.Fatal(err)
	}
	work, err := os.ReadFile("../mod/StardewMCP/FarmWork.cs")
	if err != nil {
		t.Fatal(err)
	}
	pathfinder, err := os.ReadFile("../mod/StardewMCP/Pathfinder.cs")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{
		"tile.IsTreeStump || tile.GrowthStage<5",
		"tile.HasCrop || tile.Hoed",
		"NO_SAFE_CLEARING_ROUTE",
		"CLEARING_LIMIT",
		"Destination reached after verified safe clearing",
	} {
		if !strings.Contains(string(travel), required) {
			t.Fatalf("FarmTravel missing safety contract %q", required)
		}
	}
	if !strings.Contains(string(work), `"travel" => t.Obstacle=="" && !t.IsWildTree`) ||
		!strings.Contains(string(work), `BeginFarmTravelFinal(j,now)`) {
		t.Fatal("FarmWork does not execute and verify travel tasks")
	}
	if !strings.Contains(string(pathfinder), "FindPathWithClearingCosts") ||
		!strings.Contains(string(pathfinder), "clearingCost(nx,ny)") {
		t.Fatal("weighted clearing pathfinder is not packaged")
	}
}
