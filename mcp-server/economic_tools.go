package main

import (
	"fmt"

	copilot "github.com/github/copilot-sdk/go"
)

const economicToolRules = `
ECONOMIC PLANNING (PHASE 2):
Use inspect_capability_registry before proposing a broad money-goal plan so unsupported earning methods are excluded.
Use assess_economic_state for live money, energy, inventory sale value, planted crops and active money-goal constraints.
Use analyze_crop_profit_options to compare current-season crop choices from installed game CropData and Pierre catalog data.
Use find_profit_opportunities to produce goal-aware candidates. These functions are read-only and their projections are estimates.
Phase 2 never selects or executes a strategy automatically. Before any purchase, farming or sale, inspect live prerequisites and
confirm the action is included in the goal's authorized_actions. Preserve reserve money and protected item IDs. Shop conditions,
stock, routes, weather and actual crop results remain live facts and override projections.
`

type EconomicParams struct {
	GoalID       string `json:"goal_id,omitempty" jsonschema:"Optional active money goal ID"`
	MaxTiles     int    `json:"max_tiles,omitempty" jsonschema:"Maximum proposed crop tiles, 1..64; default 16"`
	ReserveMoney int    `json:"reserve_money,omitempty" jsonschema:"Gold which projections must preserve"`
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
	}
}
