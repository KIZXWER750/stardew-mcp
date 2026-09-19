package main

import (
	"strings"
	"testing"
)

func TestCropCollision(t *testing.T) {
	s := &GameState{}
	s.Player.X = 65
	s.Player.Y = 21
	rows := make([]string, 61)
	for i := range rows {
		rows[i] = strings.Repeat(".", 61)
	}
	rows[30] = strings.Repeat(".", 31) + "C" + strings.Repeat(".", 29)
	s.Surroundings.AsciiMap = strings.Join(rows, "\n")
	a := &StardewAgent{}
	if a.isTileWalkable(s, 66, 21) {
		t.Fatal("unknown crop must not be assumed passable")
	}
	s.Surroundings.NearbyTerrainFeatures = []NearbyTerrain{{X: 66, Y: 21, Type: "hoe_dirt", HasCrop: true, IsPassable: true}}
	if !a.isTileWalkable(s, 66, 21) {
		t.Fatal("passable crop rejected")
	}
	s.Surroundings.NearbyTerrainFeatures[0].IsPassable = false
	if a.isTileWalkable(s, 66, 21) {
		t.Fatal("impassable trellis accepted")
	}
	s.Surroundings.NearbyTerrainFeatures[0].IsPassable = true
	s.Surroundings.NearbyObjects = []NearbyObject{{X: 66, Y: 21, IsPassable: false}}
	if a.isTileWalkable(s, 66, 21) {
		t.Fatal("blocking object ignored")
	}
}

func TestGameContextMakesTreeMaturityExplicit(t *testing.T) {
	s := &GameState{}
	s.Player.Location = "Farm"
	s.Player.X, s.Player.Y = 40, 10
	s.Surroundings.NearbyTerrainFeatures = []NearbyTerrain{
		{X: 43, Y: 13, Type: "tree", GrowthStage: 3, IsFullyGrown: false, CanBeChopped: false},
		{X: 46, Y: 18, Type: "tree", GrowthStage: 5, IsFullyGrown: true, CanBeChopped: true},
		{X: 41, Y: 11, Type: "fruit_tree", GrowthStage: 4, IsFullyGrown: true},
	}
	context := (&StardewAgent{}).formatGameStateContext(s)
	for _, want := range []string{
		"(43,13): type=tree growthStage=3 isFullyGrown=false canBeChopped=false",
		"(46,18): type=tree growthStage=5 isFullyGrown=true canBeChopped=true",
		"A map T or type=tree alone is insufficient",
	} {
		if !strings.Contains(context, want) {
			t.Fatalf("tree maturity context missing %q", want)
		}
	}
	if strings.Contains(context, "(41,11): type=tree") {
		t.Fatal("fruit tree was presented as an ordinary wild tree")
	}
}

func TestPlantCompletion(t *testing.T) {
	s := &GameState{}
	s.Player.Location = "Farm"
	s.Player.X = 65
	s.Player.Y = 21
	for y := 19; y <= 23; y++ {
		for x := 64; x <= 66; x++ {
			s.Surroundings.NearbyTerrainFeatures = append(s.Surroundings.NearbyTerrainFeatures, NearbyTerrain{X: x, Y: y, Type: "hoe_dirt", HasCrop: true})
		}
	}
	check := func(want int) {
		t.Helper()
		got, _ := inspectTargetPlanting(s)
		if got != want {
			t.Fatalf("got %d want %d", got, want)
		}
	}
	check(15)
	s.Surroundings.NearbyTerrainFeatures[0].HasCrop = false
	check(14)
	s.Surroundings.NearbyTerrainFeatures[1].IsDead = true
	check(13)
	s.Player.Location = "Town"
	check(0)
	s.Player.Location = "Farm"
	s.Player.X = 0
	check(0)
	if n, _ := inspectTargetPlanting(nil); n != 0 {
		t.Fatal("nil verified")
	}
}

func TestExactCompletion(t *testing.T) {
	if !isCompletion(" GOAL COMPLETE\n") {
		t.Fatal("exact completion rejected")
	}
	for _, s := range []string{"Not GOAL COMPLETE", "GOAL COMPLETE is not yet true", "TASK_BLOCKED: no path"} {
		if isCompletion(s) {
			t.Fatalf("false completion: %s", s)
		}
	}
}

func TestWaterCompletion(t *testing.T) {
	s := &GameState{}
	s.Player.Location = "Farm"
	s.Player.X = 65
	s.Player.Y = 21
	for y := 19; y <= 23; y++ {
		for x := 64; x <= 66; x++ {
			s.Surroundings.NearbyTerrainFeatures = append(s.Surroundings.NearbyTerrainFeatures, NearbyTerrain{X: x, Y: y, Type: "hoe_dirt", HasCrop: true, IsWatered: true})
		}
	}
	check := func(want int) {
		t.Helper()
		got, _ := inspectTargetWatering(s)
		if got != want {
			t.Fatalf("got %d want %d", got, want)
		}
	}
	check(15)
	s.Surroundings.NearbyTerrainFeatures[0].IsWatered = false
	check(14)
	s.Surroundings.NearbyTerrainFeatures = s.Surroundings.NearbyTerrainFeatures[1:]
	check(14)
	s.Surroundings.NearbyTerrainFeatures[0].Type = "grass"
	check(13)
	s.Player.Location = "Town"
	check(0)
	s.Player.Location = "Farm"
	s.Player.X = 0
	check(0)
	if n, _ := inspectTargetWatering(nil); n != 0 {
		t.Fatal("nil verified")
	}
}
