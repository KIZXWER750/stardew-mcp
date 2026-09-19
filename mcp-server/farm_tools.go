package main

import (
	"context"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"log"
	"strings"
	"time"
)

const farmToolRules = `
FARM GOAL EXECUTION:
Choose coordinates from observations, never ask the user to provide tile numbers.
Use find_plot_candidates for NEW plots; for existing crops inspect their observed area.
Plan the user's requested stages, execute sequentially, track verified results and stop after one completed goal.
clear_area clears only; till_plot hoes only; prepare_plot combines them; plant_plot consumes inventory seeds;
restore_tilled_soil uses a pickaxe on empty HoeDirt only and verifies it became normal ground; crops and objects are excluded;
water_plot waters eligible soil; harvest_plot harvests mature crops; remove_wild_trees removes selected ordinary
wild trees through their stumps, then detects and collects nearby loose drops. Functions handle movement and verification.
Crop observations distinguish dead=true with hasCrop=true from living crops. Dead crops never need water and cannot
be planted over. When dead crops exist in an intended work area, call remove_dead_crops first; it uses the normal
Scythe input and verifies that each dead crop disappeared before planting or watering that area.
During every crop-care visit, inspect the planted rectangle and its one-tile perimeter. If normal clearing is authorized,
remove observed weeds, grass, twigs and small stones from that perimeter before watering or leaving so spreading debris
cannot destroy crops. Preserve crops, HoeDirt, trees, buildings and placed facilities. Use small clear_area strips so each
rectangle remains within the 64-tile limit; do not clear unrelated parts of the farm.
Every observed ordinary tree includes treeState, isStump, hitsRequired for its current state,
estimatedTotalHitsToRemove and removalSequence. Use those fields instead of treating every axe obstacle as a one-hit twig.
When preserving young trees or selecting a mature tree, use only treeState=mature_tree with isStump=false,
growthStage>=5, isFullyGrown=true and canBeChopped=true. A map T or type=tree alone does not prove maturity. Never select fruit_tree.
On Farm, if ordinary move_to cannot reach a destination because of natural debris, use move_with_clearing.
It may clear only grass, weeds, twigs, small stones, young ordinary trees, and ordinary tree stumps selected by
its weighted route. It never clears mature trees, fruit trees, crops, HoeDirt, buildings, chests, machines,
furniture, fences, resource clumps, or other placed facilities. Never substitute it for a protected-path refusal.
For inventory quantity goals such as collecting wood, stone, sap, or seeds, call collect_loose_items BEFORE
removing new trees or obstacles. Pass the exact observed item ID and desired inventory quantity. Re-read its
terminal inventoryQuantityAfterCollection. Only create new drops when matching loose items were exhausted and
the desired quantity is still unmet. remove_wild_trees already collects drops created by that tree job.
For a resource quantity goal, call remove_wild_trees with max_trees=1, then inspect the returned inventory
and call collect_loose_items again before choosing another tree. Stop as soon as the inventory goal is met.
Never replay the entire chain or select a new area merely because all requested stages succeeded.
Finish with a concise report and a standalone GOAL COMPLETE on the last line only when verified.
Completed identical calls in this user goal return historical results without re-execution.
After a BLOCKED result, inspect the current tiles and recovery hint before deciding the whole goal is blocked.
If planting requires empty unhoed tiles, till_plot on the SAME authorized area preserves existing soil/crops;
then retry plant_plot. For crop-only watering, use CROPS_ONLY instead of watering unrelated empty ground.
Select smaller subrectangles when appropriate, preserving the original requested targets and reporting exclusions.
Do not replace unfinished targets with a different plot to claim completion. Do not expand beyond user scope.
Prerequisite repair is allowed only if consistent with the user's goal and prohibitions; explicit no-tilling wins.
Safe clearing requires user authorization. Ordinary clearing preserves all trees. remove_wild_trees requires explicit
tree-removal scope and always preserves fruit trees, crops, buildings, machines and placed facilities. During drop
recovery it may clear only supported ordinary wild trees, grass, weeds, small stones and twigs; never crops or facilities.
Each selected tree is one sequence: fell it, remove its stump, collect its loose drops, then select the next tree.
LOW_ENERGY/TIME_LIMIT and time-alarm interruptions persist exact unfinished tree coordinates per save/player.
A later automatic resume goal must re-observe and finish ONLY those coordinates; never choose replacement trees.
collect_loose_items and tree-drop recovery choose the farthest observed drop first and use a shortest-distance
route through supported removable natural obstacles (including ordinary mature trees), subject to the obstacle
budget and energy/time limits. Preserve fruit trees, crops, tilled soil, walls and placed facilities.
Never blindly repeat unchanged failures. There are at most 3 attempts per identical operation/area and 24 farm jobs per user goal.
Missing water, seeds, tools, energy, time or uncertain action outcomes must be reported; no unsupported recovery or cheats.
For recoverable errors fix the cause first; for unsafe/unavailable recovery report TASK_BLOCKED and stop.
All rectangles are Farm-only, northwest x/y with east width and south height, at most 64 tiles.
Before new farming work, analyze_farm_work can inspect prerequisites without acting.
When water_plot pauses with NO_WATER, remember its original rectangle and filter. Call find_water_sources,
choose a reachable observed source, refill_watering_can, then repeat water_plot for the SAME original rectangle.
The new watering job skips wet tiles. Do not restart tilling/planting, expand the plot, or claim watering complete from refill alone.
Only resume a NO_WATER pause after a verified refill. LOW_ENERGY and TIME_LIMIT may be handed to manage_daily_life
only when the user's goal authorized the corresponding recovery; all other pauses stop. Respect explicit prohibitions.
Farm-area functions do not buy seeds, eat, go home, or sleep. Use manage_daily_life between long stages and after
recoverable PAUSED results when the user's goal authorizes food, returning home, or ending the day.
If a Farm-area function returns PREREQUISITE_REQUIRED for WRONG_LOCATION, move to the required location and retry it. No farm action was recorded or attempted, so this location-only retry is safe.
`

type PlotParams struct {
	RequestID          string `json:"request_id,omitempty" jsonschema:"Optional idempotency key; reuse only for the identical request in this game session"`
	SeedItemID         string `json:"seed_item_id,omitempty" jsonschema:"Required for plant_plot: exact inventory seed item ID, e.g. (O)472; never guess"`
	ExistingCropPolicy string `json:"existing_crop_policy,omitempty" jsonschema:"PRESERVE_AND_REPORT default, or REQUIRE_SAME_CROP"`

	Location           string `json:"location" jsonschema:"The current map; this version requires Farm"`
	X                  int    `json:"x" jsonschema:"Northwest tile X"`
	Y                  int    `json:"y" jsonschema:"Northwest tile Y"`
	Width              int    `json:"width" jsonschema:"Number of columns"`
	Height             int    `json:"height" jsonschema:"Number of rows; total area at most 64"`
	MinimumEnergy      int    `json:"minimum_energy,omitempty" jsonschema:"Energy reserve; default and minimum 20"`
	StopTime           int    `json:"stop_time,omitempty" jsonschema:"Game HHMM deadline; default and latest 2200"`
	TargetFilter       string `json:"target_filter,omitempty" jsonschema:"ALL_HOED_SOIL default or CROPS_ONLY for water"`
	MaxTrees           int    `json:"max_trees,omitempty" jsonschema:"For remove_wild_trees only: maximum selected ordinary trees/stumps, default 3, range 1..12"`
	PreserveYoungTrees bool   `json:"preserve_young_trees,omitempty" jsonschema:"For remove_wild_trees only: preserve ordinary trees with growthStage below 5. For true, select only an observed type=tree with growthStage>=5/isFullyGrown=true; never infer maturity from map T or type alone. Default false"`
	MaxObstacles       int    `json:"max_obstacles,omitempty" jsonschema:"For move_with_clearing only: maximum natural obstacles removed; default 8, range 1..16"`
	DropItemID         string `json:"item_id,omitempty" jsonschema:"For collect_loose_items: exact observed item ID; empty means any collectible loose item"`
	DropSearchRadius   int    `json:"search_radius,omitempty" jsonschema:"For collect_loose_items: player-centered radius, default 20, range 1..30"`
	DesiredQuantity    int    `json:"desired_inventory_quantity,omitempty" jsonschema:"For collect_loose_items: stop once inventory reaches this quantity; 0 collects all matching drops"`
}

type ClearingMoveParams struct {
	RequestID     string `json:"request_id,omitempty" jsonschema:"Optional idempotency key; reuse only for the identical request in this game session"`
	Location      string `json:"location" jsonschema:"Current map; this version requires Farm"`
	X             int    `json:"x" jsonschema:"Observed destination tile X; never ask the user for coordinates"`
	Y             int    `json:"y" jsonschema:"Observed destination tile Y; never ask the user for coordinates"`
	MinimumEnergy int    `json:"minimum_energy,omitempty" jsonschema:"Energy reserve; default and minimum 20"`
	StopTime      int    `json:"stop_time,omitempty" jsonschema:"Game HHMM deadline; default and latest 2200"`
	MaxObstacles  int    `json:"max_obstacles,omitempty" jsonschema:"Maximum removable route obstacles; default 8, range 1..16"`
}

type CollectLooseParams struct {
	RequestID                string `json:"request_id,omitempty"`
	Location                 string `json:"location"`
	ItemID                   string `json:"item_id,omitempty"`
	SearchRadius             int    `json:"search_radius,omitempty"`
	DesiredInventoryQuantity int    `json:"desired_inventory_quantity,omitempty"`
	MinimumEnergy            int    `json:"minimum_energy,omitempty"`
	StopTime                 int    `json:"stop_time,omitempty"`
	MaxObstacles             int    `json:"max_obstacles,omitempty"`
}

func (p CollectLooseParams) plot() PlotParams {
	return PlotParams{RequestID: p.RequestID, Location: p.Location, X: 0, Y: 0, Width: 1, Height: 1,
		MinimumEnergy: p.MinimumEnergy, StopTime: p.StopTime, MaxObstacles: p.MaxObstacles,
		DropItemID: p.ItemID, DropSearchRadius: p.SearchRadius, DesiredQuantity: p.DesiredInventoryQuantity}
}

func (p ClearingMoveParams) plot() PlotParams {
	return PlotParams{RequestID: p.RequestID, Location: p.Location, X: p.X, Y: p.Y, Width: 1, Height: 1,
		MinimumEnergy: p.MinimumEnergy, StopTime: p.StopTime, MaxObstacles: p.MaxObstacles}
}

type CandidateParams struct {
	Location      string `json:"location" jsonschema:"Current map; must be Farm"`
	AnchorX       int    `json:"anchor_x" jsonschema:"Observed anchor tile X, usually player or a known landmark"`
	AnchorY       int    `json:"anchor_y" jsonschema:"Observed anchor tile Y"`
	Direction     string `json:"direction,omitempty" jsonschema:"ANY, NORTH, SOUTH, EAST or WEST relative to anchor"`
	Width         int    `json:"width" jsonschema:"Requested rectangle columns"`
	Height        int    `json:"height" jsonschema:"Requested rectangle rows"`
	SearchRadius  int    `json:"search_radius,omitempty" jsonschema:"Radius 1..12; default 8"`
	MaxCandidates int    `json:"max_candidates,omitempty" jsonschema:"Return 1..20; default 8"`
}

func (p CandidateParams) values() (map[string]interface{}, error) {
	if p.Location != "Farm" {
		return nil, fmt.Errorf("location must be Farm")
	}
	if p.Width < 1 || p.Height < 1 || p.Width > 64 || p.Height > 64 || p.Width*p.Height > 64 {
		return nil, fmt.Errorf("rectangle must contain 1..64 tiles")
	}
	radius := p.SearchRadius
	if radius == 0 {
		radius = 8
	}
	if radius < 1 || radius > 12 {
		return nil, fmt.Errorf("search_radius must be 1..12")
	}
	limit := p.MaxCandidates
	if limit == 0 {
		limit = 8
	}
	if limit < 1 || limit > 20 {
		return nil, fmt.Errorf("max_candidates must be 1..20")
	}
	direction := p.Direction
	if direction == "" {
		direction = "ANY"
	}
	switch direction {
	case "ANY", "NORTH", "SOUTH", "EAST", "WEST":
	default:
		return nil, fmt.Errorf("invalid direction")
	}
	return map[string]interface{}{"location": p.Location, "anchor_x": p.AnchorX, "anchor_y": p.AnchorY, "direction": direction,
		"width": p.Width, "height": p.Height, "search_radius": radius, "max_candidates": limit}, nil
}

func (p PlotParams) validate() error {
	if p.ExistingCropPolicy != "" && p.ExistingCropPolicy != "PRESERVE_AND_REPORT" && p.ExistingCropPolicy != "REQUIRE_SAME_CROP" {
		return fmt.Errorf("invalid existing_crop_policy")
	}
	if p.Location != "Farm" {
		return fmt.Errorf("location must be Farm; move there first")
	}
	if p.X < 0 || p.Y < 0 || p.Width < 1 || p.Height < 1 || p.Width > 64 || p.Height > 64 || p.Width*p.Height > 64 {
		return fmt.Errorf("rectangle must be nonnegative and contain 1..64 tiles")
	}
	if p.StopTime != 0 && (p.StopTime < 600 || p.StopTime > 2200 || p.StopTime%100 >= 60) {
		return fmt.Errorf("invalid stop_time")
	}
	if p.TargetFilter != "" && p.TargetFilter != "ALL_HOED_SOIL" && p.TargetFilter != "CROPS_ONLY" {
		return fmt.Errorf("invalid target_filter")
	}
	if p.MaxTrees < 0 || p.MaxTrees > 12 {
		return fmt.Errorf("max_trees must be 1..12 when provided")
	}
	if p.MaxObstacles < 0 || p.MaxObstacles > 16 {
		return fmt.Errorf("max_obstacles must be 1..16 when provided")
	}
	if p.DropSearchRadius < 0 || p.DropSearchRadius > 30 {
		return fmt.Errorf("search_radius must be 1..30 when provided")
	}
	if p.DesiredQuantity < 0 || p.DesiredQuantity > 9999 {
		return fmt.Errorf("desired_inventory_quantity must be 0..9999")
	}
	return nil
}

func (p PlotParams) values(op string) map[string]interface{} {
	energy := p.MinimumEnergy
	if energy < 20 {
		energy = 20
	}
	deadline := p.StopTime
	if deadline == 0 {
		deadline = 2200
	}
	filter := p.TargetFilter
	if filter == "" {
		filter = "ALL_HOED_SOIL"
	}
	maxTrees := p.MaxTrees
	if maxTrees == 0 {
		maxTrees = 3
	}
	maxObstacles := p.MaxObstacles
	if maxObstacles == 0 {
		maxObstacles = 8
	}
	searchRadius := p.DropSearchRadius
	if searchRadius == 0 {
		searchRadius = 20
	}
	return map[string]interface{}{"location": p.Location, "x": p.X, "y": p.Y, "width": p.Width, "height": p.Height,
		"request_id": p.RequestID, "seed_item_id": p.SeedItemID, "existing_crop_policy": p.ExistingCropPolicy, "operation": op, "minimum_energy": energy, "stop_time": deadline, "target_filter": filter,
		"max_trees": maxTrees, "preserve_young_trees": p.PreserveYoungTrees, "max_obstacles": maxObstacles,
		"item_id": p.DropItemID, "search_radius": searchRadius, "desired_inventory_quantity": p.DesiredQuantity}
}

type farmResult struct {
	TaskID    string `json:"taskId"`
	Status    string `json:"status"`
	Reason    string `json:"reason"`
	Total     int    `json:"totalTargets"`
	Completed int    `json:"completedTargets"`
	Remaining int    `json:"remainingTargets"`
	Uses      int    `json:"toolUses"`
}

func decodeFarm(r *WebSocketResponse) (farmResult, string, error) {
	var result farmResult
	if r == nil {
		return result, "", fmt.Errorf("missing game response")
	}
	if !r.Success {
		return result, "", fmt.Errorf("%s", r.Message)
	}
	b, e := json.Marshal(r.Data)
	if e != nil {
		return result, "", e
	}
	if e = json.Unmarshal(b, &result); e != nil {
		return result, "", e
	}
	if result.TaskID == "" || result.Status == "" {
		return result, "", fmt.Errorf("missing farm task state; install updated game DLL")
	}
	switch result.Status {
	case "RUNNING", "COMPLETED", "PAUSED", "BLOCKED", "FAILED", "CANCELLED":
	default:
		return result, "", fmt.Errorf("unknown task status")
	}
	if result.Total < 0 || result.Completed < 0 || result.Completed > result.Total || result.Remaining != result.Total-result.Completed {
		return result, "", fmt.Errorf("inconsistent farm progress")
	}
	if result.Status == "COMPLETED" && (result.Completed != result.Total || result.Remaining != 0) {
		return result, "", fmt.Errorf("inconsistent completion counts")
	}
	return result, string(b), nil
}

func (a *StardewAgent) inspectFarmArea(p PlotParams) (string, error) {
	if e := p.validate(); e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	r, e := gameClient.SendCommand("farm_inspect", p.values(""))
	if e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	if r == nil || !r.Success {
		return "TASK_BLOCKED: farm inspection failed", nil
	}
	b, e := json.Marshal(r.Data)
	return string(b), e
}

func (a *StardewAgent) findPlotCandidates(p CandidateParams) (string, error) {
	values, e := p.values()
	if e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	r, e := gameClient.SendCommand("farm_find_candidates", values)
	if e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	if r == nil || !r.Success {
		return "TASK_BLOCKED: candidate search failed", nil
	}
	b, e := json.Marshal(r.Data)
	return string(b), e
}

func (a *StardewAgent) runFarmArea(op string, p PlotParams) (string, error) {
	if e := p.validate(); e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	if op == "plant" && p.SeedItemID == "" {
		return "TASK_BLOCKED: seed_item_id is required", nil
	}
	if preflight, stop := farmLocationPreflight(p, gameClient.GetState()); stop {
		return preflight, nil
	}
	a.toolMutex.Lock()
	defer a.toolMutex.Unlock()
	key := farmKey(op, p)
	if op == "refill" {
		key = a.refillKey(key)
	}
	if cached, err := a.beginFarmAttempt(key); err != nil {
		return "TASK_BLOCKED: " + err.Error(), nil
	} else if cached != "" {
		return cached, nil
	}
	finished := false
	defer func() {
		if !finished {
			a.endFarmAttempt(key, "FAILED", "Uncertain execution outcome; inspect before any new work.")
		}
	}()
	a.requestMu.Lock()
	parent := a.farmContext
	a.requestMu.Unlock()
	if parent == nil {
		parent = context.Background()
	}
	ctx, cancel := context.WithTimeout(parent, 5*time.Minute+10*time.Second)
	defer cancel()
	if ctx.Err() != nil {
		return "TASK_BLOCKED: request cancelled", nil
	}
	if p.RequestID == "" || a.farmTransportAttempt(key) > 1 {
		var id [16]byte
		if _, e := rand.Read(id[:]); e != nil {
			return "", e
		}
		p.RequestID = fmt.Sprintf("%x", id)
	}
	r, e := gameClient.SendCommand("farm_start", p.values(op))
	if e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	result, body, e := decodeFarm(r)
	if e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	id := result.TaskID
	terminal := false
	defer func() {
		if !terminal {
			_, _ = gameClient.SendCommand("farm_cancel", map[string]interface{}{"task_id": id})
		}
	}()
	ticker := time.NewTicker(250 * time.Millisecond)
	defer ticker.Stop()
	previous := ""
	for {
		summary := fmt.Sprintf("%s %s %d/%d uses=%d", op, result.Status, result.Completed, result.Total, result.Uses)
		if summary != previous {
			log.Printf("[FARM TASK] %s id=%s", summary, id)
			previous = summary
		}
		if result.Status != "RUNNING" {
			terminal = true
			body = withRecoveryHint(body, result, op)
			a.endFarmAttempt(key, result.Status, body)
			finished = true
			return body, nil
		}
		select {
		case <-ctx.Done():
			return "TASK_BLOCKED: farm request cancelled or timed out; partial changes remain", nil
		case <-ticker.C:
		}
		r, e = gameClient.SendCommand("farm_status", map[string]interface{}{"task_id": id})
		if e != nil {
			return "TASK_BLOCKED: farm status connection error: " + e.Error(), nil
		}
		result, body, e = decodeFarm(r)
		if e != nil {
			return "TASK_BLOCKED: " + e.Error(), nil
		}
	}
}

func farmLocationPreflight(p PlotParams, state *GameState) (string, bool) {
	if state == nil {
		return `{"status":"PREREQUISITE_REQUIRED","reason":"GAME_STATE_UNAVAILABLE","actionMayHaveExecuted":false,"nextAction":"Wait for a fresh game-state observation, then retry."}`, true
	}
	required := strings.TrimSpace(p.Location)
	if required != "" && state.Player.Location != required {
		body, _ := json.Marshal(map[string]interface{}{
			"status": "PREREQUISITE_REQUIRED", "reason": "WRONG_LOCATION",
			"currentLocation": state.Player.Location, "requiredLocation": required,
			"actionMayHaveExecuted": false,
			"nextAction":            "Move to the required location, verify it from fresh state, then retry the identical farm function. This preflight did not enter the idempotency ledger.",
		})
		return string(body), true
	}
	return "", false
}
