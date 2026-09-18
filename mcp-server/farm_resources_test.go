package main

import (
	"encoding/json"
	"testing"
)

func TestAnalysisReadOnlyRequestValidation(t *testing.T) {
	p := AnalyzeParams{PlotParams: PlotParams{Location: "Farm", Width: 2, Height: 2}, Operation: "water"}
	v, e := p.valuesForAnalysis()
	if e != nil || v["operation"] != "water" {
		t.Fatal(v, e)
	}
	p.Operation = "plant"
	if _, e := p.valuesForAnalysis(); e == nil {
		t.Fatal("plant analysis without seed accepted")
	}
	p.SeedItemID = "(O)472"
	b, e := json.Marshal(p)
	if e != nil {
		t.Fatal(e)
	}
	var fields map[string]interface{}
	json.Unmarshal(b, &fields)
	if fields["location"] != "Farm" || fields["operation"] != "plant" {
		t.Fatal("embedded parameters not flattened", fields)
	}
	p.Operation = "cheat"
	if _, e := p.valuesForAnalysis(); e == nil {
		t.Fatal("invalid analysis operation")
	}
}

func TestWaterSourceSearchBounds(t *testing.T) {
	v, e := (WaterSourceParams{}).values()
	if e != nil || v["search_radius"] != 32 || v["max_sources"] != 8 {
		t.Fatal(v, e)
	}
	for _, p := range []WaterSourceParams{{SearchRadius: -1}, {SearchRadius: 65}, {MaxSources: 21}} {
		if _, e := p.values(); e == nil {
			t.Fatal("invalid search accepted", p)
		}
	}
}

func TestWaterPauseRequiresVerifiedRefill(t *testing.T) {
	a := &StardewAgent{}
	p := PlotParams{Location: "Farm", Width: 2, Height: 2}
	key := farmKey("water", p)
	a.beginFarmAttempt(key)
	a.endFarmAttempt(key, "PAUSED", `{"reason":"NO_WATER","toolUses":2}`)
	if _, e := a.beginFarmAttempt(key); e == nil {
		t.Fatal("resumed without water")
	}
	a.beginFarmAttempt("unrelated")
	a.endFarmAttempt("unrelated", "COMPLETED", `{}`)
	if _, e := a.beginFarmAttempt(key); e == nil {
		t.Fatal("unrelated work unlocked water pause")
	}
	refill := a.refillKey(farmKey("refill", p))
	a.beginFarmAttempt(refill)
	a.endFarmAttempt(refill, "COMPLETED", `{"waterBefore":0,"waterAfter":40}`)
	if cached, e := a.beginFarmAttempt(key); e != nil || cached != "" {
		t.Fatal("verified refill did not unlock watering", cached, e)
	}
}

func TestRefillNotReplayedUntilWaterUsed(t *testing.T) {
	a := &StardewAgent{}
	base := farmKey("refill", PlotParams{Location: "Farm", Width: 1, Height: 1})
	key := a.refillKey(base)
	a.beginFarmAttempt(key)
	a.endFarmAttempt(key, "COMPLETED", `{}`)
	if a.refillKey(base) != key {
		t.Fatal("same refill unexpectedly acquired new identity")
	}
	cached, e := a.beginFarmAttempt(a.refillKey(base))
	if e != nil || cached == "" {
		t.Fatal("refill replayed", e)
	}
	water := farmKey("water", PlotParams{Location: "Farm", Width: 2, Height: 2})
	a.beginFarmAttempt(water)
	a.endFarmAttempt(water, "PAUSED", `{"reason":"NO_WATER","toolUses":3}`)
	if a.refillKey(base) == key {
		t.Fatal("water consumption did not enable later refill")
	}
}

func TestRefillCannotResumeOtherPauses(t *testing.T) {
	for _, reason := range []string{"LOW_ENERGY", "TIME_LIMIT", "CLIENT_HEARTBEAT_LOST"} {
		a := &StardewAgent{}
		p := PlotParams{Location: "Farm", Width: 1, Height: 1}
		key := farmKey("water", p)
		a.beginFarmAttempt(key)
		a.endFarmAttempt(key, "PAUSED", `{"reason":"`+reason+`"}`)
		refill := a.refillKey(farmKey("refill", p))
		a.beginFarmAttempt(refill)
		a.endFarmAttempt(refill, "COMPLETED", `{}`)
		if _, e := a.beginFarmAttempt(key); e == nil {
			t.Fatal("unsafe pause resumed", reason)
		}
	}
	if !recoveryHint(farmResult{Status: "PAUSED", Reason: "NO_WATER"}, "water").Candidate {
		t.Fatal("water recovery missing")
	}
}
