package main

import (
	"encoding/json"
	"fmt"
	"strings"
)

type farmAttempt struct {
	Reason      string
	RefillEpoch int
	Attempts    int
	Epoch       int
	Status      string
	Body        string
}

// Transport IDs and budgets do not change the identity of a requested action.
// A new user prompt creates a new agent/ledger; SDK response boundaries do not.
func farmKey(op string, p PlotParams) string {
	filter := p.TargetFilter
	if op != "water" {
		filter = ""
	} else if filter == "" {
		filter = "ALL_HOED_SOIL"
	}
	seed, policy := "", ""
	if op == "plant" {
		seed = strings.TrimPrefix(p.SeedItemID, "(O)")
		policy = p.ExistingCropPolicy
		if policy == "" {
			policy = "PRESERVE_AND_REPORT"
		}
	}
	b, _ := json.Marshal([]interface{}{op, p.Location, p.X, p.Y, p.Width, p.Height, seed, policy, filter})
	return string(b)
}

func (a *StardewAgent) beginFarmAttempt(key string) (string, error) {
	a.requestMu.Lock()
	defer a.requestMu.Unlock()
	if a.farmLedger == nil {
		a.farmLedger = map[string]farmAttempt{}
	}
	item, exists := a.farmLedger[key]
	if exists {
		if item.Status == "COMPLETED" {
			return item.Body + "\nALREADY_COMPLETED_THIS_GOAL: historical verified result; no new action was sent. Continue only unfinished stages, or end the goal.", nil
		}
		waterPause := item.Status == "PAUSED" && item.Reason == "NO_WATER" && strings.HasPrefix(key, `["water",`)
		if item.Status != "BLOCKED" && !waterPause {
			return "", fmt.Errorf("PREVIOUS_%s: do not repeat cancelled, paused, active or uncertain work; report the existing result", item.Status)
		}
		if waterPause && a.farmRefillEpoch <= item.RefillEpoch {
			return "", fmt.Errorf("REFILL_REQUIRED: verify a watering-can refill before resuming the original plot")
		}
		if item.Epoch >= a.farmEpoch {
			return "", fmt.Errorf("UNCHANGED_FAILURE: inspect and complete an authorized prerequisite repair before retrying this operation")
		}
		if item.Attempts >= 3 {
			return "", fmt.Errorf("RECOVERY_LIMIT: three attempts used for this operation and area")
		}
	}
	if a.farmActions >= 24 {
		return "", fmt.Errorf("GOAL_ACTION_LIMIT: stop and report verified partial progress")
	}
	a.farmActions++
	item.Attempts++
	item.Epoch = a.farmEpoch
	item.RefillEpoch = a.farmRefillEpoch
	item.Status = "RUNNING"
	a.farmLedger[key] = item
	return "", nil
}

func (a *StardewAgent) endFarmAttempt(key, status, body string) {
	a.requestMu.Lock()
	defer a.requestMu.Unlock()
	item := a.farmLedger[key]
	var result farmResult
	_ = json.Unmarshal([]byte(body), &result)
	item.Reason = result.Reason
	if strings.HasPrefix(key, `["water",`) && result.Uses > 0 {
		a.farmWaterGeneration++
	}
	if status == "COMPLETED" {
		a.farmEpoch++
		if strings.HasPrefix(key, `["refill",`) {
			a.farmRefillEpoch++
		}
	}
	item.Status = status
	item.Body = body
	item.Epoch = a.farmEpoch
	item.RefillEpoch = a.farmRefillEpoch
	a.farmLedger[key] = item
}

// A repaired attempt is a new game command; never reuse the failed transport
// idempotency key, even if the caller supplied one for the logical operation.
func (a *StardewAgent) farmTransportAttempt(key string) int {
	a.requestMu.Lock()
	defer a.requestMu.Unlock()
	return a.farmLedger[key].Attempts
}

type farmRecovery struct {
	Candidate         bool   `json:"candidate"`
	RequiresUserScope bool   `json:"requiresUserScope"`
	NextSteps         string `json:"nextSteps"`
}

func recoveryHint(result farmResult, op string) farmRecovery {
	hint := farmRecovery{RequiresUserScope: true, NextSteps: "Report the unresolved condition. Do not blindly repeat or remove protected objects."}
	if op == "water" && result.Status == "PAUSED" && result.Reason == "NO_WATER" {
		return farmRecovery{Candidate: true, RequiresUserScope: true, NextSteps: "Keep original plot and filter. If refilling is not forbidden, find_water_sources, refill_watering_can at an observed reachable source, then water_plot SAME plot/filter. Wet tiles are skipped. Do not restart other completed stages. Stop if source/refill cannot be verified."}
	}
	if result.Status != "BLOCKED" {
		return hint
	}
	if strings.HasPrefix(result.Reason, "NOT_HOED") {
		hint.Candidate = true
		if op == "water" {
			hint.NextSteps = "Inspect original tiles. For watering crops only use CROPS_ONLY, excluding bare ground. If the user requested preparing all these tiles, till_plot empty diggable tiles within the same authorized area, then retry. Never alter crops or expand scope."
		} else if op == "plant" {
			hint.NextSteps = "Inspect original tiles. If planting implies preparing this empty diggable soil and tilling is not forbidden, call till_plot for the same area (existing soil and crops are preserved), then retry plant_plot. Otherwise report the missing prerequisite."
		}
	} else if strings.HasPrefix(result.Reason, "CLEARING_REQUIRED") {
		hint.Candidate = true
		hint.NextSteps = "Inspect the supported obstacle. If clearing is authorized by the user, clear_area within the original scope, then till_plot if needed, then retry the failed stage. If clearing was prohibited, report that restriction. Never remove protected crops or facilities."
	} else if strings.Contains(result.Reason, "no safe approach") || strings.Contains(result.Reason, "NO_SAFE_APPROACH") {
		hint.Candidate = true
		hint.NextSteps = "Inspect target approaches. Try a different reachable side or smaller subrectangle within original scope; only clear supported obstacles if user authorized clearing. Preserve original unresolved targets; do not substitute another plot or loop without progress."
	}
	return hint
}

func withRecoveryHint(body string, result farmResult, op string) string {
	var data map[string]interface{}
	if json.Unmarshal([]byte(body), &data) != nil {
		return body
	}
	if result.Status == "COMPLETED" {
		data["nextAction"] = "This stage is verified complete. Continue only other unfinished user-requested stages. If all stages are done, report results then put GOAL COMPLETE on the last line and stop."
	} else {
		data["recovery"] = recoveryHint(result, op)
	}
	b, err := json.Marshal(data)
	if err != nil {
		return body
	}
	return string(b)
}
