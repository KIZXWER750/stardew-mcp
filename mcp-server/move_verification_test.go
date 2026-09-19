package main

import (
	"strings"
	"testing"
)

func TestMoveCompletionDoesNotUseOldCache(t *testing.T) {
	c := &GameClient{state: &GameState{Player: PlayerState{X: 57, Y: 19, Location: "Farm"}}, responses: make(map[string]chan *WebSocketResponse)}
	ch := make(chan *WebSocketResponse, 1)
	c.responses["move1"] = ch
	c.handleCommandResponse(&WebSocketResponse{ID: "move1", Success: true, State: &GameState{Player: PlayerState{X: 61, Y: 19, Location: "Farm"}}})
	resp := <-ch
	if c.GetState().Player.X != 61 {
		t.Fatal("state was not updated before tool returned")
	}
	if !strings.HasPrefix(verifyMoveCompletion(resp, "Farm", 61, 19), "Arrived") {
		t.Fatal("actual arrival rejected")
	}
}

func TestMoveCompletionRejectsUnverifiedResults(t *testing.T) {
	cases := []*WebSocketResponse{
		nil,
		{Success: true},
		{Success: false, Message: "no path"},
		{Success: true, State: &GameState{Player: PlayerState{X: 57, Y: 19, Location: "Farm"}}},
		{Success: true, State: &GameState{Player: PlayerState{X: 61, Y: 19, Location: "Town"}}},
	}
	for _, resp := range cases {
		if !strings.HasPrefix(verifyMoveCompletion(resp, "Farm", 61, 19), "TASK_BLOCKED:") {
			t.Fatal("unverified movement accepted")
		}
	}
}
