package main

import (
	"os"
	"strings"
	"testing"
)

func TestBedtimeAlarmPolicyContract(t *testing.T) {
	agentBytes, err := os.ReadFile("../mod/StardewMCP/IngameAgent.cs")
	if err != nil {
		t.Fatal(err)
	}
	agent := string(agentBytes)
	for _, required := range []string{
		"FirstBedtimeAlarm {get;set;}=2200", "SecondBedtimeAlarm {get;set;}=2400", "FinalBedtimeAlarm {get;set;}=2500",
		"AUTOMATIC TIME DECISION ALARM", "get_surroundings and assess_daily_status",
		"Returning home is NOT mandatory", "pendingBedtimeLevel==1 && !executor.HasActiveGameplayAction",
		"Busy && level>=2", "InterruptForBedtimeAlarm", "TryStartPendingBedtime",
	} {
		if !strings.Contains(agent, required) {
			t.Fatal("alarm policy missing: " + required)
		}
	}
	entryBytes, err := os.ReadFile("../mod/StardewMCP/ModEntry.cs")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(entryBytes), "GameLoop.TimeChanged += OnTimeChanged") {
		t.Fatal("time event missing")
	}
	controlBytes, err := os.ReadFile("../mod/StardewMCP/IngameControl.cs")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(controlBytes), "HasActiveGameplayAction") {
		t.Fatal("safe-point signal missing")
	}
}

func TestMorningGateReceivesNormalInputWhileNewDayIsTrue(t *testing.T) {
	transitionBytes, err := os.ReadFile("../mod/StardewMCP/SleepTransition.cs")
	if err != nil {
		t.Fatal(err)
	}
	transition := string(transitionBytes)
	for _, required := range []string{
		"[MORNING GATE]", "[MORNING INPUT]", "[MORNING VERIFIED]",
		"Game1.player.hasMoved && timePasses", "_helper.Input.Press(morningButton)",
		"Game1.options.moveLeftButton", "Game1.options.moveRightButton",
		"Game1.options.moveDownButton", "Game1.options.moveUpButton",
	} {
		if !strings.Contains(transition, required) {
			t.Fatal("morning input contract missing: " + required)
		}
	}
	if strings.Contains(transition, "Game1.globalFade || Game1.newDay") {
		t.Fatal("newDay still blocks the input required to release the 6 AM gate")
	}
}
