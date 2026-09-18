package main

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"

	copilot "github.com/github/copilot-sdk/go"
)

const lifeToolRules = `
DAILY LIFE AND RECOVERY:
Use assess_daily_status before deciding whether to continue work, recover, return home, or sleep.
Use find_recovery_options and find_food_options before consuming anything. Food use requires the exact observed
slot and item ID, and a reserve quantity. Never consume a quest item, requested item, last specimen, valuable crop,
or user-protected item merely because it is edible. consume_food performs one normal, verified consumption only.
find_home_route is read-only. return_home follows observed loaded-map exits with normal movement and verifies FarmHouse;
it never teleports. If no route or approach is verified, stop instead of guessing coordinates.
schedule_bedtime is an estimate, not proof of travel time. Keep extra margin for obstacles and menus.
sleep_until_morning is valid only inside FarmHouse. It resolves the actual player bed, approaches only from the left
or right, accepts only the verified Sleep confirmation, and completes only after the next morning is observed.
Spa recovery is intentionally reported as unavailable in this release until its changing-room route is verified.
Never manipulate time, energy, health, location, inventory, or sleep state through cheats.
`

type LifeEmptyParams struct{}
type ConsumeFoodParams struct {
	Slot            int    `json:"slot" jsonschema:"Exact slot returned by find_food_options"`
	ItemID          string `json:"item_id" jsonschema:"Exact qualified item ID returned by find_food_options"`
	ReserveQuantity int    `json:"reserve_quantity" jsonschema:"Minimum total quantity of this item to retain after eating; zero only when explicitly safe"`
}
type BedtimeParams struct {
	TargetBedTime int `json:"target_bed_time,omitempty" jsonschema:"Desired in-bed game time HHMM; default 2400, range 1800..2500"`
	BufferMinutes int `json:"buffer_minutes,omitempty" jsonschema:"Extra safety margin in game minutes; default 30, range 10..180"`
}

type lifeReply struct {
	Status string `json:"status"`
	Reason string `json:"reason"`
}

func lifeCommand(action string, values map[string]interface{}) (lifeReply, string, error) {
	var decoded lifeReply
	r, err := gameClient.SendCommand(action, values)
	if err != nil {
		return decoded, "", err
	}
	if r == nil || !r.Success {
		if r == nil {
			return decoded, "", fmt.Errorf("missing game response")
		}
		return decoded, "", fmt.Errorf("%s", r.Message)
	}
	b, err := json.Marshal(r.Data)
	if err != nil {
		return decoded, "", err
	}
	if err = json.Unmarshal(b, &decoded); err != nil {
		return decoded, "", err
	}
	return decoded, string(b), nil
}

func (a *StardewAgent) consumeFood(p ConsumeFoodParams) (string, error) {
	if p.Slot < 0 || p.Slot > 35 || strings.TrimSpace(p.ItemID) == "" || p.ReserveQuantity < 0 {
		return "TASK_BLOCKED: invalid food slot, item ID, or reserve quantity", nil
	}
	a.toolMutex.Lock()
	defer a.toolMutex.Unlock()
	values := map[string]interface{}{"reset": true, "slot": p.Slot, "item_id": p.ItemID, "reserve_quantity": p.ReserveQuantity}
	deadline := time.Now().Add(14 * time.Second)
	for time.Now().Before(deadline) {
		state, raw, err := lifeCommand("life_eat_step", values)
		values = map[string]interface{}{}
		if err != nil {
			return "TASK_BLOCKED: " + err.Error(), nil
		}
		switch state.Status {
		case "COMPLETED":
			return raw, nil
		case "BLOCKED", "FAILED":
			return "TASK_BLOCKED: " + raw, nil
		case "RUNNING":
			time.Sleep(150 * time.Millisecond)
		default:
			return "TASK_BLOCKED: invalid eating task state", nil
		}
	}
	return "TASK_BLOCKED: eating verification timed out; do not retry blindly", nil
}

type homeRoute struct {
	Status string `json:"status"`
	Route  []struct {
		ExitID     string `json:"exitId"`
		From       string `json:"from"`
		To         string `json:"to"`
		Approaches []struct {
			X int `json:"x"`
			Y int `json:"y"`
		} `json:"approaches"`
	} `json:"route"`
}

func readLifeRoute(destination string) (homeRoute, string, error) {
	var route homeRoute
	r, err := gameClient.SendCommand("shop_route", map[string]interface{}{"destination": destination})
	if err != nil {
		return route, "", err
	}
	if r == nil || !r.Success {
		return route, "", fmt.Errorf("home route observation failed")
	}
	b, err := json.Marshal(r.Data)
	if err != nil {
		return route, "", err
	}
	if err = json.Unmarshal(b, &route); err != nil {
		return route, "", err
	}
	return route, string(b), nil
}

func readHomeRoute() (homeRoute, string, error) {
	state := gameClient.GetState()
	if state == nil {
		return homeRoute{}, "", fmt.Errorf("game disconnected")
	}
	if state.Player.Location == "FarmHouse" {
		raw := `{"status":"COMPLETED","location":"FarmHouse","routeToFarm":[],"finalLeg":"already inside"}`
		return homeRoute{Status: "COMPLETED"}, raw, nil
	}
	finalLeg := `{"from":"Farm","to":"FarmHouse","stand":{"x":64,"y":15},"door":{"x":64,"y":14},"action":"normal north interaction","walkablePorch":"x=59..66 at y=15; x=63..65 at y=16","onlyOutsideEntries":[{"from":{"x":63,"y":17},"to":{"x":63,"y":16}},{"from":{"x":64,"y":17},"to":{"x":64,"y":16}},{"from":{"x":65,"y":17},"to":{"x":65,"y":16}}]}`
	if state.Player.Location == "Farm" {
		raw := fmt.Sprintf(`{"status":"OBSERVED","location":"Farm","routeToFarm":[],"finalLeg":%s}`, finalLeg)
		return homeRoute{Status: "OBSERVED"}, raw, nil
	}
	route, rawToFarm, err := readLifeRoute("Farm")
	if err != nil {
		return route, "", err
	}
	raw := fmt.Sprintf(`{"status":%q,"location":%q,"routeToFarm":%s,"finalLeg":%s}`, route.Status, state.Player.Location, rawToFarm, finalLeg)
	return route, raw, nil
}

func (a *StardewAgent) returnHome() (string, error) {
	a.toolMutex.Lock()
	defer a.toolMutex.Unlock()
	visited := []string{}
	for transition := 0; transition < 12; transition++ {
		state := gameClient.GetState()
		if state == nil {
			return "TASK_BLOCKED: game disconnected", nil
		}
		if state.Player.Location == "FarmHouse" {
			return fmt.Sprintf(`{"status":"COMPLETED","location":"FarmHouse","x":%d,"y":%d,"transitions":%d,"visited":%q}`, state.Player.X, state.Player.Y, transition, strings.Join(visited, " -> ")), nil
		}
		if state.Player.Location == "Farm" {
			_, _ = a.doMoveTo(64, 15)
			current := gameClient.GetState()
			if current == nil || current.Player.Location != "Farm" || current.Player.X != 64 || current.Player.Y != 15 {
				return "TASK_BLOCKED: could not reach the verified farmhouse approach (64,15); inspect porch obstacles before retrying", nil
			}
			response, err := gameClient.SendCommand("life_enter_farmhouse", nil)
			if err != nil || response == nil || !response.Success {
				if err != nil {
					return "TASK_BLOCKED: farmhouse interaction failed: " + err.Error(), nil
				}
				return "TASK_BLOCKED: farmhouse interaction was rejected", nil
			}
			deadline := time.Now().Add(5 * time.Second)
			for time.Now().Before(deadline) {
				current = gameClient.GetState()
				if current != nil && current.Player.Location == "FarmHouse" {
					visited = append(visited, "Farm->FarmHouse")
					return fmt.Sprintf(`{"status":"COMPLETED","location":"FarmHouse","x":%d,"y":%d,"transitions":%d,"visited":%q}`, current.Player.X, current.Player.Y, transition+1, strings.Join(visited, " -> ")), nil
				}
				time.Sleep(100 * time.Millisecond)
			}
			return "TASK_BLOCKED: farmhouse door input did not produce a verified FarmHouse transition", nil
		}
		route, _, err := readLifeRoute("Farm")
		if err != nil || route.Status == "BLOCKED" || len(route.Route) == 0 {
			if err != nil {
				return "TASK_BLOCKED: " + err.Error(), nil
			}
			return fmt.Sprintf("TASK_BLOCKED: no observed loaded-map route from %s to Farm", state.Player.Location), nil
		}
		edge := route.Route[0]
		if edge.From != state.Player.Location || edge.ExitID == "" {
			return "TASK_BLOCKED: stale or inconsistent home route; refresh before retrying", nil
		}
		arrived := false
		for _, approach := range edge.Approaches {
			_, _ = a.doMoveTo(approach.X, approach.Y)
			current := gameClient.GetState()
			if current != nil && current.Player.Location == edge.From && current.Player.X == approach.X && current.Player.Y == approach.Y {
				arrived = true
				break
			}
		}
		if !arrived {
			return fmt.Sprintf("TASK_BLOCKED: no returned approach is reachable for %s -> %s", edge.From, edge.To), nil
		}
		response, err := gameClient.SendCommand("shop_exit", map[string]interface{}{"exit_id": edge.ExitID})
		if err != nil || response == nil || !response.Success {
			if err != nil {
				return "TASK_BLOCKED: home exit failed: " + err.Error(), nil
			}
			return fmt.Sprintf("TASK_BLOCKED: home exit rejected for %s -> %s", edge.From, edge.To), nil
		}
		current := gameClient.GetState()
		if current == nil || current.Player.Location != edge.To {
			return fmt.Sprintf("TASK_BLOCKED: transition not verified; expected %s after leaving %s", edge.To, edge.From), nil
		}
		visited = append(visited, edge.From+"->"+edge.To)
	}
	return "TASK_BLOCKED: home route exceeded 12 verified transitions", nil
}

func hhmmMinutes(v int) (int, bool) {
	h, m := v/100, v%100
	if h < 0 || h > 26 || m < 0 || m >= 60 {
		return 0, false
	}
	return h*60 + m, true
}
func minutesHHMM(v int) int { return (v/60)*100 + v%60 }

func scheduleBedtime(p BedtimeParams) (string, error) {
	target := p.TargetBedTime
	if target == 0 {
		target = 2400
	}
	buffer := p.BufferMinutes
	if buffer == 0 {
		buffer = 30
	}
	targetMinutes, valid := hhmmMinutes(target)
	if !valid || target < 1800 || target > 2500 || buffer < 10 || buffer > 180 {
		return "TASK_BLOCKED: invalid bedtime or safety buffer", nil
	}
	state := gameClient.GetState()
	if state == nil {
		return "TASK_BLOCKED: game disconnected", nil
	}
	route, _, err := readHomeRoute()
	if err != nil {
		return "TASK_BLOCKED: " + err.Error(), nil
	}
	hops := len(route.Route)
	travelEstimate := hops*30 + 20
	depart := targetMinutes - buffer - travelEstimate
	now, _ := hhmmMinutes(state.Time.TimeOfDay)
	status := "PLANNED"
	if now >= depart {
		status = "RETURN_HOME_NOW"
	}
	return fmt.Sprintf(`{"status":%q,"currentTime":%d,"targetBedTime":%d,"routeHops":%d,"estimatedTravelMinutes":%d,"safetyBufferMinutes":%d,"recommendedDeparture":%d,"note":"Estimate only; obstacles and menus can take longer."}`, status, state.Time.TimeOfDay, target, hops, travelEstimate, buffer, minutesHHMM(depart)), nil
}

func (a *StardewAgent) defineLifeTools() []copilot.Tool {
	return []copilot.Tool{
		copilot.DefineTool("assess_daily_status", "Read current time, energy, health, location and conservative next-action advice. No game action.", func(p LifeEmptyParams, inv copilot.ToolInvocation) (string, error) {
			return farmReadCommand("life_status", nil)
		}),
		copilot.DefineTool("find_food_options", "Read positively restorative inventory food with exact slot, item ID, stack and estimated recovery. No consumption.", func(p LifeEmptyParams, inv copilot.ToolInvocation) (string, error) {
			return farmReadCommand("life_food_options", nil)
		}),
		copilot.DefineTool("find_recovery_options", "Read safe recovery choices: observed food, verified sleep availability after returning home, and current spa automation support. No action.", func(p LifeEmptyParams, inv copilot.ToolInvocation) (string, error) {
			return farmReadCommand("life_recovery_options", nil)
		}),
		copilot.DefineTool("consume_food", "Consume exactly one observed positive-recovery food through normal input and verify inventory/recovery changes. Requires an explicit reserve quantity.", func(p ConsumeFoodParams, inv copilot.ToolInvocation) (string, error) { return a.consumeFood(p) }),
		copilot.DefineTool("find_home_route", "Read the loaded-map route from the current location to FarmHouse. No movement. Returned coordinates are internal navigation data, not user input requirements.", func(p LifeEmptyParams, inv copilot.ToolInvocation) (string, error) {
			_, raw, err := readHomeRoute()
			if err != nil {
				return "TASK_BLOCKED: " + err.Error(), nil
			}
			return raw, nil
		}),
		copilot.DefineTool("return_home", "Follow observed route exits with normal movement until FarmHouse is verified. No teleporting and no sleeping.", func(p LifeEmptyParams, inv copilot.ToolInvocation) (string, error) { return a.returnHome() }),
		copilot.DefineTool("schedule_bedtime", "Estimate a conservative departure time from the observed route hop count. Read-only; the estimate is not proof of travel duration.", func(p BedtimeParams, inv copilot.ToolInvocation) (string, error) { return scheduleBedtime(p) }),
		copilot.DefineTool("sleep_until_morning", "Inside FarmHouse, find the actual player bed, approach from its left or right side, enter it, accept only the Sleep confirmation, and verify the next morning.", func(p LifeEmptyParams, inv copilot.ToolInvocation) (string, error) { return a.sleepUntilMorning() }),
	}
}
