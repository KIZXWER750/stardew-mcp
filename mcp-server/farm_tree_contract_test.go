package main

import (
	"os"
	"strings"
	"testing"
)

func TestTreeDropGameContractIsPackaged(t *testing.T) {
	work, err := os.ReadFile("../mod/StardewMCP/FarmWork.cs")
	if err != nil {
		t.Fatal(err)
	}
	treeDrops, err := os.ReadFile("../mod/StardewMCP/FarmTreeDrops.cs")
	if err != nil {
		t.Fatal(err)
	}
	serializer, err := os.ReadFile("../mod/StardewMCP/GameStateSerializer.cs")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range []string{
		"t.IsTreeStump?5:t.GrowthStage>=5?10:4",
		"BeginTreeDropCollection(j,now)",
		"item.Tile.GrowthStage>=5 && !item.Tile.IsTreeStump?0:item.Tile.IsTreeStump?1:2",
		"j.Targets=rankedTrees.Take(j.MaxTrees)",
		"NO_ELIGIBLE_TREE",
		"growth_stage={tile.GrowthStage}",
	} {
		if !strings.Contains(string(work), required) {
			t.Fatalf("FarmWork missing tree contract %q", required)
		}
	}
	for _, required := range []string{
		"currentLocation.debris.Contains",
		"couldInventoryAcceptThisItem",
		"DROP_DISAPPEARED_WITHOUT_INVENTORY_GAIN",
		"DROP_UNREACHABLE_PROTECTED_PATH",
		"ObstaclesClearedForDrops>=j.MaxDropAccessObstacles",
		"DropMoveAttempts<12",
		"DropItemsCollected+=gained",
	} {
		if !strings.Contains(string(treeDrops), required) {
			t.Fatalf("FarmTreeDrops missing drop contract %q", required)
		}
	}
	if !strings.Contains(string(serializer), "foreach (var loose in location.debris.ToList())") ||
		!strings.Contains(string(serializer), "Source = \"location_debris\"") {
		t.Fatal("loose game debris is not included in observations")
	}
}
