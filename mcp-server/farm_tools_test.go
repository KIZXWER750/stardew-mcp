package main

import "testing"

func TestPlotValidationAndDefaults(t *testing.T) {
	p := PlotParams{Location: "Farm", X: 3, Y: 4, Width: 3, Height: 5}
	if e := p.validate(); e != nil {
		t.Fatal(e)
	}
	v := p.values("prepare")
	if v["minimum_energy"] != 20 || v["stop_time"] != 2200 || v["max_trees"] != 3 {
		t.Fatal("unsafe defaults")
	}
	for _, bad := range []PlotParams{
		{Location: "Town", Width: 3, Height: 5},
		{Location: "Farm", Width: 9, Height: 9},
		{Location: "Farm", Width: 0, Height: 1},
		{Location: "Farm", X: -1, Width: 1, Height: 1},
		{Location: "Farm", Width: 1, Height: 1, StopTime: 2165},
		{Location: "Farm", Width: 1, Height: 1, MaxTrees: 13},
	} {
		if bad.validate() == nil {
			t.Fatalf("accepted invalid area: %+v", bad)
		}
	}
}

func TestTreeRequestIdentityIncludesSafetyScope(t *testing.T) {
	p := PlotParams{Location: "Farm", X: 1, Y: 2, Width: 5, Height: 5, MaxTrees: 3}
	q := p
	q.MaxTrees = 4
	if farmKey("trees", p) == farmKey("trees", q) {
		t.Fatal("different tree limits shared an idempotency key")
	}
	q = p
	q.IncludeSaplings = true
	if farmKey("trees", p) == farmKey("trees", q) {
		t.Fatal("sapling authorization missing from idempotency key")
	}
}

func TestFarmRejectsFalseCompletion(t *testing.T) {
	for _, status := range []string{"RUNNING", "PAUSED", "BLOCKED", "CANCELLED", "FAILED"} {
		r := &WebSocketResponse{Success: true, Data: map[string]interface{}{"taskId": "a", "status": status, "totalTargets": 15, "completedTargets": 14, "remainingTargets": 1}}
		result, _, e := decodeFarm(r)
		if e != nil || result.Status != status {
			t.Fatal("lost partial status", e)
		}
	}
	bad := &WebSocketResponse{Success: true, Data: map[string]interface{}{"taskId": "a", "status": "COMPLETED", "totalTargets": 15, "completedTargets": 14, "remainingTargets": 1}}
	if _, _, e := decodeFarm(bad); e == nil {
		t.Fatal("false completion accepted")
	}
	good := &WebSocketResponse{Success: true, Data: map[string]interface{}{"taskId": "a", "status": "COMPLETED", "totalTargets": 15, "completedTargets": 15, "remainingTargets": 0}}
	if _, _, e := decodeFarm(good); e != nil {
		t.Fatal(e)
	}
}

func TestCandidateDefaultsAndValidation(t *testing.T) {
	good := CandidateParams{Location: "Farm", AnchorX: 50, AnchorY: 20, Width: 5, Height: 3}
	values, e := good.values()
	if e != nil {
		t.Fatal(e)
	}
	if values["search_radius"] != 8 || values["max_candidates"] != 8 || values["direction"] != "ANY" {
		t.Fatalf("bad defaults: %#v", values)
	}
	for _, bad := range []CandidateParams{
		{Location: "Town", Width: 1, Height: 1},
		{Location: "Farm", Width: 0, Height: 1},
		{Location: "Farm", Width: 9, Height: 9},
		{Location: "Farm", Width: 1, Height: 1, SearchRadius: 13},
		{Location: "Farm", Width: 1, Height: 1, MaxCandidates: 21},
	} {
		if _, e := bad.values(); e == nil {
			t.Fatalf("accepted invalid candidate request: %+v", bad)
		}
	}
}
