package main

import "testing"

func TestStage2CropPolicyAndPayload(t *testing.T) {
	p := PlotParams{Location: "Farm", Width: 2, Height: 2, SeedItemID: "(O)472", ExistingCropPolicy: "REQUIRE_SAME_CROP", RequestID: "same-request"}
	if e := p.validate(); e != nil {
		t.Fatal(e)
	}
	v := p.values("plant")
	if v["seed_item_id"] != "(O)472" || v["existing_crop_policy"] != "REQUIRE_SAME_CROP" || v["request_id"] != "same-request" {
		t.Fatal(v)
	}
	p.ExistingCropPolicy = "DESTROY_EXISTING"
	if p.validate() == nil {
		t.Fatal("destructive crop policy accepted")
	}
}

func TestStage2RejectImpossibleProgress(t *testing.T) {
	for _, data := range []map[string]interface{}{
		{"taskId": "t", "status": "RUNNING", "totalTargets": 2, "completedTargets": 3, "remainingTargets": -1},
		{"taskId": "t", "status": "BLOCKED", "totalTargets": 2, "completedTargets": 1, "remainingTargets": 0},
		{"taskId": "t", "status": "COMPLETED", "totalTargets": -1, "completedTargets": -1, "remainingTargets": 0},
	} {
		if _, _, err := decodeFarm(&WebSocketResponse{Success: true, Data: data}); err == nil {
			t.Fatalf("accepted %#v", data)
		}
	}
}

func TestStage2CandidateBounds(t *testing.T) {
	for _, p := range []CandidateParams{
		{Location: "Farm", Width: 1, Height: 1, Direction: "DOWN"},
		{Location: "Farm", Width: int(^uint(0) >> 1), Height: 2},
	} {
		if _, e := p.values(); e == nil {
			t.Fatal("invalid candidate accepted")
		}
	}
}
