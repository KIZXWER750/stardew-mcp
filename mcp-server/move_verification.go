package main

import "fmt"

// A successful command reply is sent by the mod when movement completes,
// not when it starts. Never replace it with a pre-command cached position.
func verifyMoveCompletion(resp *WebSocketResponse, location string, x, y int) string {
    if resp == nil { return "TASK_BLOCKED: missing movement response" }
    if !resp.Success { return fmt.Sprintf("TASK_BLOCKED: move rejected: %s", resp.Message) }
    if resp.State == nil {
        return "TASK_BLOCKED: move responded but command-time state is missing. Install the updated StardewMCP mod and restart SMAPI; do not repeat movement blindly. Response: " + resp.Message
    }
    p := resp.State.Player
    if p.Location != location {
        return fmt.Sprintf("TASK_BLOCKED: location changed during movement: expected=%s actual=%s position=(%d,%d). Inspect the new location.", location, p.Location, p.X, p.Y)
    }
    if p.X != x || p.Y != y {
        return fmt.Sprintf("TASK_BLOCKED: command-time arrival mismatch: target=(%d,%d) actual=(%d,%d). Response: %s", x,y,p.X,p.Y,resp.Message)
    }
    return fmt.Sprintf("Arrived at destination. Verified command-time state: %s (%d,%d). Facing=%s. Refresh surroundings before the next action.", p.Location,p.X,p.Y,p.FacingDirectionName)
}
