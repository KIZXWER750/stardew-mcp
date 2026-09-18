package main

import (
	"strings"
	"testing"
)

func TestCompletionReportAndFinalMarker(t *testing.T) {
	for _, s := range []string{"GOAL COMPLETE", "수확, 파종, 물주기 완료.\nGOAL COMPLETE", "모두 확인했습니다.\r\nGOAL COMPLETE\r\n"} {
		if !isCompletion(s) {
			t.Fatalf("valid final report rejected: %q", s)
		}
	}
	for _, s := range []string{"GOAL COMPLETE\n아직 남았습니다", "not GOAL COMPLETE", "TASK_BLOCKED: failed\nGOAL COMPLETE", "완료할 수 없습니다"} {
		if isCompletion(s) {
			t.Fatalf("false completion accepted: %q", s)
		}
	}
}

func TestWholeChainCannotReplayInSameGoal(t *testing.T) {
	a := &StardewAgent{}
	p := PlotParams{Location: "Farm", X: 1, Y: 2, Width: 2, Height: 2, SeedItemID: "(O)472"}
	for _, op := range []string{"harvest", "till", "plant", "water"} {
		key := farmKey(op, p)
		if cached, e := a.beginFarmAttempt(key); e != nil || cached != "" {
			t.Fatal(cached, e)
		}
		a.endFarmAttempt(key, "COMPLETED", op+" verified")
	}
	before := a.farmActions
	for _, op := range []string{"harvest", "till", "plant", "water"} {
		cached, e := a.beginFarmAttempt(farmKey(op, p))
		if e != nil || !strings.Contains(cached, "ALREADY_COMPLETED_THIS_GOAL") {
			t.Fatal(cached, e)
		}
	}
	if a.farmActions != before {
		t.Fatal("completed chain scheduled more actions")
	}
}

func TestMissingSoilRepairThenRetry(t *testing.T) {
	a := &StardewAgent{}
	plant, till := "plant-area", "till-area"
	if _, e := a.beginFarmAttempt(plant); e != nil {
		t.Fatal(e)
	}
	a.endFarmAttempt(plant, "BLOCKED", "NOT_HOED")
	if _, e := a.beginFarmAttempt(plant); e == nil {
		t.Fatal("unchanged failure retried")
	}
	if _, e := a.beginFarmAttempt(till); e != nil {
		t.Fatal(e)
	}
	a.endFarmAttempt(till, "COMPLETED", "soil repaired")
	if cached, e := a.beginFarmAttempt(plant); e != nil || cached != "" {
		t.Fatal("repaired planting rejected", cached, e)
	}
	a.endFarmAttempt(plant, "COMPLETED", "planted")
}

func TestRetriesAndUnknownOutcomesAreBounded(t *testing.T) {
	a := &StardewAgent{}
	for n := 0; n < 3; n++ {
		if _, e := a.beginFarmAttempt("target"); e != nil {
			t.Fatal(e)
		}
		a.endFarmAttempt("target", "BLOCKED", "failure")
		key := string(rune('a' + n))
		if _, e := a.beginFarmAttempt(key); e != nil {
			t.Fatal(e)
		}
		a.endFarmAttempt(key, "COMPLETED", "repair")
	}
	if _, e := a.beginFarmAttempt("target"); e == nil {
		t.Fatal("fourth attempt accepted")
	}
	b := &StardewAgent{}
	b.beginFarmAttempt("uncertain")
	b.endFarmAttempt("uncertain", "FAILED", "timeout")
	b.beginFarmAttempt("other")
	b.endFarmAttempt("other", "COMPLETED", "done")
	if _, e := b.beginFarmAttempt("uncertain"); e == nil {
		t.Fatal("uncertain action blindly replayed")
	}
}

func TestNormalizedIdentityAndNewGoal(t *testing.T) {
	p := PlotParams{Location: "Farm", Width: 2, Height: 2, SeedItemID: "472"}
	q := p
	q.SeedItemID = "(O)472"
	q.ExistingCropPolicy = "PRESERVE_AND_REPORT"
	q.RequestID = "different"
	if farmKey("plant", p) != farmKey("plant", q) {
		t.Fatal("equivalent requests bypass deduplication")
	}
	a := &StardewAgent{}
	a.beginFarmAttempt("area")
	a.endFarmAttempt("area", "COMPLETED", "done")
	b := &StardewAgent{}
	if cached, e := b.beginFarmAttempt("area"); e != nil || cached != "" {
		t.Fatal("new user goal incorrectly blocked")
	}
}

func TestRecoveryDoesNotAuthorizeProtectedDestruction(t *testing.T) {
	for _, reason := range []string{"NOT_HOED (1,2)", "CLEARING_REQUIRED (1,2): grass"} {
		hint := recoveryHint(farmResult{Status: "BLOCKED", Reason: reason}, "plant")
		if !hint.Candidate || !hint.RequiresUserScope {
			t.Fatal(hint)
		}
	}
	for _, reason := range []string{"PROTECTED chest", "EXISTING_CROP_CONFLICT", "NO_VERIFIED_CHANGE", "TOOL_RESULT_TIMEOUT"} {
		if recoveryHint(farmResult{Status: "BLOCKED", Reason: reason}, "plant").Candidate {
			t.Fatal("unsafe repair suggested", reason)
		}
	}
}
