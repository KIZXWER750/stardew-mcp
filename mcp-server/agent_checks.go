package main

import (
	"fmt"
	"strings"
	"time"
)

func isCompletion(text string) bool {
	lines := strings.Split(strings.TrimSpace(text), "\n")
	for _, line := range lines {
		if strings.HasPrefix(strings.TrimSpace(line), "TASK_BLOCKED:") {
			return false
		}
	}
	return strings.TrimSpace(lines[len(lines)-1]) == "GOAL COMPLETE"
}

// Crop symbols do not encode collision. Require positive terrain evidence.
func observedCropWalkable(state *GameState, x, y int) bool {
	if state == nil {
		return false
	}
	for _, obj := range state.Surroundings.NearbyObjects {
		if obj.X == x && obj.Y == y && !obj.IsPassable {
			return false
		}
	}
	for _, tf := range state.Surroundings.NearbyTerrainFeatures {
		if tf.X == x && tf.Y == y {
			return tf.Type == "hoe_dirt" && tf.HasCrop && tf.IsPassable
		}
	}
	return false
}

// A new snapshot is required, not just a delay followed by an old cached state.
func freshGameState() *GameState {
	previous := gameClient.GetState()
	deadline := time.Now().Add(4 * time.Second)
	for time.Now().Before(deadline) {
		time.Sleep(100 * time.Millisecond)
		current := gameClient.GetState()
		if current != nil && current != previous {
			return current
		}
	}
	return nil
}

// This opt-in planting check counts living crops, not bare tilled soil.
// It verifies occupancy, not crop species: pre-existing crops are preserved.
func inspectTargetPlanting(state *GameState) (int, string) {
	if state == nil || state.Player.Location != "Farm" {
		return 0, "Planting UNCONFIRMED: no fresh Farm observation."
	}
	terrain := make(map[[2]int]NearbyTerrain)
	for _, tf := range state.Surroundings.NearbyTerrainFeatures {
		terrain[[2]int{tf.X, tf.Y}] = tf
	}
	count := 0
	var details strings.Builder
	for y := 19; y <= 23; y++ {
		for x := 64; x <= 66; x++ {
			tf, found := terrain[[2]int{x, y}]
			dx, dy := x-state.Player.X, y-state.Player.Y
			inRange := dx >= -30 && dx <= 30 && dy >= -30 && dy <= 30
			valid := found && inRange && tf.Type == "hoe_dirt" && tf.HasCrop && !tf.IsDead
			if valid {
				count++
			}
			fmt.Fprintf(&details, "(%d,%d): confirmed=%v terrain=%s hasCrop=%v dead=%v passable=%v crop=%s observed=%v\n", x, y, valid, tf.Type, tf.HasCrop, tf.IsDead, tf.IsPassable, tf.CropName, found && inRange)
		}
	}
	return count, fmt.Sprintf("PLANT CHECK Farm X=64..66 Y=19..23: %d/15 living crops. Preserve existing crops; plant only observed empty hoe_dirt.\n%s", count, details.String())
}

// Verify actual watered soil, independent of the agent completion claim.
func inspectTargetWatering(state *GameState) (int, string) {
	if state == nil || state.Player.Location != "Farm" {
		return 0, "Watering UNCONFIRMED: no fresh Farm observation."
	}
	terrain := make(map[[2]int]NearbyTerrain)
	for _, tf := range state.Surroundings.NearbyTerrainFeatures {
		terrain[[2]int{tf.X, tf.Y}] = tf
	}
	count := 0
	var details strings.Builder
	for y := 19; y <= 23; y++ {
		for x := 64; x <= 66; x++ {
			tf, found := terrain[[2]int{x, y}]
			dx, dy := x-state.Player.X, y-state.Player.Y
			inRange := dx >= -30 && dx <= 30 && dy >= -30 && dy <= 30
			valid := found && inRange && tf.Type == "hoe_dirt" && tf.IsWatered
			if valid {
				count++
			}
			fmt.Fprintf(&details, "(%d,%d): confirmed=%v terrain=%s hasCrop=%v dead=%v passable=%v crop=%s watered=%v observed=%v\n", x, y, valid, tf.Type, tf.HasCrop, tf.IsDead, tf.IsPassable, tf.CropName, tf.IsWatered, found && inRange)
		}
	}
	return count, fmt.Sprintf("WATER CHECK Farm X=64..66 Y=19..23: %d/15 watered hoe_dirt tiles. Water only dry target tiles using the watering can. Skip watered tiles. Preserve crops.\n%s", count, details.String())
}
