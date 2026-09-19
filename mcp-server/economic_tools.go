package main

import (
	"fmt"

	copilot "github.com/github/copilot-sdk/go"
)

const economicToolRules = `
ECONOMIC PLANNING:
Use inspect_capability_registry before proposing a broad money-goal plan so unsupported earning methods are excluded.
Use assess_economic_state for live money, energy, inventory sale value, planted crops and active money-goal constraints.
Use analyze_crop_profit_options to compare current-season crop choices from installed game CropData and Pierre catalog data.
Use find_profit_opportunities to produce goal-aware candidates. These functions are read-only and their projections are estimates.
Phase 3 selects and persists a strategy through build_goal_plan. Phase 4 executes it only through leased goal-plan steps. Before any purchase, farming or sale, inspect live prerequisites and
confirm the action is included in the goal's authorized_actions. Preserve reserve money and protected item IDs. Shop conditions,
stock, routes, weather and actual crop results remain live facts and override projections.
Use inspect_goal_plan before relying on a saved plan. If stale=true, call refresh_goal_plan. A blocked plan with
requiresUserInput=true must be followed by request_goal_input so the dedicated in-game response window collects authorization.
Pass its suggestedQuestion and suggestedOptions unchanged so the saved answer can be validated against the named actions.
Never treat a generated plan, candidate ranking, or suggested question as permission. Only start_goal_plan_execution may begin an already authorized current plan.
An empty sellable inventory is a missing prerequisite, not completion. Continue to build_goal_plan so live existing crops can be considered before proposing seed purchase or new planting. tend_existing_crops covers only watering and harvesting crops that are already planted. If the user's explicit restrictions leave no executable candidate, use the dedicated request_goal_input flow instead of ending with an ordinary chat-only block.
Live crop tiles with dead=true are withered crops, not growing crops. Never water them or treat them as plantable soil. A crop-cycle plan must remove them with remove_dead_crops before preparing, planting, or watering that plot.
If build_goal_plan or refresh_goal_plan returns an internal data or command failure, do not bypass the persistent plan by calling raw mutating farm, shop, or sleep tools. Stop with the exact failure so the saved goal remains intact for repair and retry.
`

type EconomicParams struct {
	GoalID       string `json:"goal_id,omitempty" jsonschema:"Optional active money goal ID"`
	MaxTiles     int    `json:"max_tiles,omitempty" jsonschema:"Maximum proposed crop tiles, 1..64; default 16"`
	ReserveMoney int    `json:"reserve_money,omitempty" jsonschema:"Gold which projections must preserve"`
}

type GoalPlanParams struct {
	GoalID   string `json:"goal_id" jsonschema:"Active persistent money goal ID"`
	MaxTiles int    `json:"max_tiles,omitempty" jsonschema:"Maximum planned crop tiles, 1..64; default 16"`
}

func (p GoalPlanParams) values() (map[string]interface{}, error) {
	if p.GoalID == "" || p.MaxTiles < 0 || p.MaxTiles > 64 {
		return nil, fmt.Errorf("goal_id required and max_tiles must be 1..64 when provided")
	}
	return map[string]interface{}{"goal_id": p.GoalID, "max_tiles": p.MaxTiles}, nil
}

func (p EconomicParams) values() (map[string]interface{}, error) {
	if p.MaxTiles < 0 || p.MaxTiles > 64 || p.ReserveMoney < 0 {
		return nil, fmt.Errorf("max_tiles must be 1..64 when provided and reserve_money must be nonnegative")
	}
	return map[string]interface{}{"goal_id": p.GoalID, "max_tiles": p.MaxTiles, "reserve_money": p.ReserveMoney}, nil
}

func (a *StardewAgent) defineEconomicTools() []copilot.Tool {
	return []copilot.Tool{
		copilot.DefineTool("inspect_capability_registry", "Read the exact supported, unavailable and future economic/gameplay capabilities plus currently held farm tools. Read-only.", func(_ ShopEmptyParams, _ copilot.ToolInvocation) (string, error) {
			return farmReadCommand("capability_registry", nil)
		}),
		copilot.DefineTool("assess_economic_state", "Read live money, date/time, energy, inventory sell values, planted crop state and active money goals. Stale chest memory is intentionally excluded. Read-only.", func(_ ShopEmptyParams, _ copilot.ToolInvocation) (string, error) {
			return farmReadCommand("economic_state", nil)
		}),
		copilot.DefineTool("analyze_crop_profit_options", "Compare current-season crop options using installed CropData, live sell prices, owned seeds and installed Pierre catalog prices. Returns cost, harvest count, revenue, profit and work estimates; does not act.", func(p EconomicParams, _ copilot.ToolInvocation) (string, error) {
			v, err := p.values()
			if err != nil {
				return "TASK_BLOCKED: " + err.Error(), nil
			}
			return farmReadCommand("crop_profit_options", v)
		}),
		copilot.DefineTool("find_profit_opportunities", "Build goal-aware, executable-with-supported-tools profit candidates from live inventory and crop economics. Explicitly reports unsupported strategies. Read-only; never selects or runs a strategy.", func(p EconomicParams, _ copilot.ToolInvocation) (string, error) {
			v, err := p.values()
			if err != nil {
				return "TASK_BLOCKED: " + err.Error(), nil
			}
			return farmReadCommand("profit_opportunities", v)
		}),
		copilot.DefineTool("build_goal_plan", "Select one supported profit strategy using the goal preference, reserve, deadline, action authorization and protected items; persist an ordered date-based plan. Returns a dedicated-question recommendation if authorization is missing. Does not execute steps.", func(p GoalPlanParams, _ copilot.ToolInvocation) (string, error) {
			v, err := p.values()
			if err != nil {
				return "TASK_BLOCKED: " + err.Error(), nil
			}
			return farmReadCommand("goal_plan_build", v)
		}),
		copilot.DefineTool("inspect_goal_plan", "Read a persisted goal strategy and dated steps, and compare its planning fingerprint with current money, date, inventory and constraints. Does not execute steps.", func(p GoalPlanParams, _ copilot.ToolInvocation) (string, error) {
			v, err := p.values()
			if err != nil {
				return "TASK_BLOCKED: " + err.Error(), nil
			}
			return farmReadCommand("goal_plan_inspect", v)
		}),
		copilot.DefineTool("refresh_goal_plan", "Recompute and replace a stale or blocked goal plan from current live economic state. Rechecks authorization and deadline. Does not execute steps.", func(p GoalPlanParams, _ copilot.ToolInvocation) (string, error) {
			v, err := p.values()
			if err != nil {
				return "TASK_BLOCKED: " + err.Error(), nil
			}
			return farmReadCommand("goal_plan_refresh", v)
		}),
	}
}
