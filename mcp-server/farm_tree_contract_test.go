package main

import (
	"encoding/json"
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
	for _, required := range []string{
		"featureInfo.IsStump = tree.stump.Value",
		"featureInfo.TreeState = GetTreeState(tree)",
		"featureInfo.IsFullyGrown = !tree.stump.Value",
		"featureInfo.EstimatedTotalHitsToRemove",
		"featureInfo.RemovalSequence",
		"if (tree.stump.Value)",
		"if (obj.Name.Contains(\"Twig\")) return \"twig\"",
	} {
		if !strings.Contains(string(serializer), required) {
			t.Fatalf("GameStateSerializer missing explicit tree-state contract %q", required)
		}
	}
}

func TestTwigAndTreeStatesStayDistinctInAgentContext(t *testing.T) {
	state := &GameState{}
	state.Player.X, state.Player.Y = 10, 10
	state.Surroundings.NearbyObjects = []NearbyObject{{X: 11, Y: 10, Name: "Twig", DisplayName: "Twig", Type: "twig", RequiredTool: "Axe", HitsRequired: 1}}
	state.Surroundings.NearbyTerrainFeatures = []NearbyTerrain{{X: 12, Y: 10, Type: "tree", TreeState: "mature_tree", RequiredTool: "Axe", HitsRequired: 10, EstimatedTotalHitsToRemove: 15, RemovalSequence: "fell_then_remove_stump"}}
	context := (&StardewAgent{}).formatGameStateContext(state)
	for _, required := range []string{"state=twig mapObject=true tool=Axe hits=1", "treeState=mature_tree", "estimatedTotalHitsToRemove=15"} {
		if !strings.Contains(context, required) {
			t.Fatalf("twig/tree distinction missing %q: %s", required, context)
		}
	}
}

func TestObservedTreeStateReachesAgentContext(t *testing.T) {
	var state GameState
	payload := `{"player":{"x":10,"y":10},"surroundings":{"nearbyTerrainFeatures":[{"x":12,"y":10,"type":"tree","growthStage":5,"isStump":true,"treeState":"stump","isFullyGrown":false,"canBeChopped":false,"requiredTool":"Axe","hitsRequired":5,"estimatedTotalHitsToRemove":5,"removalSequence":"axe_repeatedly_until_terrain_feature_disappears"}]}}`
	if err := json.Unmarshal([]byte(payload), &state); err != nil {
		t.Fatal(err)
	}
	if len(state.Surroundings.NearbyTerrainFeatures) != 1 {
		t.Fatal("tree observation was not decoded")
	}
	tree := state.Surroundings.NearbyTerrainFeatures[0]
	if !tree.IsStump || tree.TreeState != "stump" || tree.IsFullyGrown || tree.CanBeChopped || tree.HitsRequired != 5 || tree.EstimatedTotalHitsToRemove != 5 {
		t.Fatalf("tree state lost during decode: %+v", tree)
	}
	context := (&StardewAgent{}).formatGameStateContext(&state)
	for _, required := range []string{"treeState=stump", "isStump=true", "currentStateHits=5", "estimatedTotalHitsToRemove=5", "repeated-hit terrain feature"} {
		if !strings.Contains(context, required) {
			t.Fatalf("agent context missing %q: %s", required, context)
		}
	}
}
