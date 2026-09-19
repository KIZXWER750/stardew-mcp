package main

import (
	"strings"

	copilot "github.com/github/copilot-sdk/go"
)

const craftingToolRules = `
CRAFTING AND MATERIAL PROCUREMENT:
For a request to craft an item, first call inspect_crafting_recipe. Its installed-game recipe, known-recipe state, live ingredient counts and output capacity are authoritative.
Before choosing how to obtain each missing ingredient, call lookup_game_knowledge for the ingredient with type=materials. Prefer the lowest-effort currently feasible method in its acquisitionMethods, then verify the live source before acting.
Search remembered chests before gathering new materials. A remembered chest is only a lead: inspect the live closed chest, move to it, open it and take only the required quantity. Preserve user-protected items and chest purposes.
Use normal gameplay tools for procurement. Examples include clear_area or clear_target for weeds and stones, remove_wild_trees for wood, and storage tools for known supplies. Never use cheat tools or claim an unsupported gathering method exists.
Before executing the procurement sequence, assess live time, energy, inventory capacity, route and opening-hour constraints. Order the work by prerequisites, batch actions in the same location, avoid repeated trips, reserve enough time and energy for required travel, and keep the requested output slot available.
After any material count, inventory capacity, location, time, tool availability or source condition changes, re-observe the affected state and revise the remaining sequence. Keep already verified work; do not restart the whole crafting plan.
If the knowledge dictionary has no usable acquisition method, or two feasible methods have a meaningful cost, destruction, time or resource tradeoff with no clear lowest-effort choice, ask once through request_player_input (or request_goal_input for a persistent goal). Include the alternatives and keep the original crafting context. Do not ask when one safe method is clearly easiest.
Re-inspect the recipe after acquiring or moving materials. Call craft_item only with that fresh observation ID and a new request ID. Never replay an uncertain craft request; inspect inventory and recipe state first.
Craft intermediate ingredients recursively only when their recipe is known and the required source is authorized. Stop after verifying the requested output quantity in inventory.
`

type CraftInspectParams struct {
	Recipe   string `json:"recipe" jsonschema:"Requested recipe or output item name"`
	Quantity int    `json:"quantity,omitempty" jsonschema:"Number of recipe crafts, default 1, maximum 99"`
}

type CraftExecuteParams struct {
	Recipe      string `json:"recipe" jsonschema:"Exact recipe name returned by inspect_crafting_recipe"`
	Quantity    int    `json:"quantity" jsonschema:"Number of recipe crafts, 1..99"`
	Observation string `json:"observation_id" jsonschema:"Fresh observationId returned by inspect_crafting_recipe"`
	RequestID   string `json:"request_id" jsonschema:"Unique 8..128 character id for this exact irreversible craft attempt"`
}

func (a *StardewAgent) defineCraftingTools() []copilot.Tool {
	return []copilot.Tool{
		copilot.DefineTool("inspect_crafting_recipe", "Inspect one known installed-game crafting recipe, requested quantity, live inventory ingredients, missing amounts and output capacity. Read-only. Use before planning material procurement and again immediately before crafting.", func(p CraftInspectParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.Recipe) == "" || p.Quantity < 0 || p.Quantity > 99 {
				return "TASK_BLOCKED: recipe required and quantity must be 1..99 when provided", nil
			}
			quantity := p.Quantity
			if quantity == 0 {
				quantity = 1
			}
			return farmReadCommand("crafting_inspect", map[string]interface{}{"recipe": p.Recipe, "quantity": quantity})
		}),
		copilot.DefineTool("craft_item", "Craft an observed known recipe using normal recipe consumption and inventory placement. Requires a fresh observation and unique request ID; verifies the output and protects against duplicate execution.", func(p CraftExecuteParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.Recipe) == "" || p.Quantity < 1 || p.Quantity > 99 || len(p.Observation) < 8 || len(p.RequestID) < 8 || len(p.RequestID) > 128 {
				return "TASK_BLOCKED: exact recipe, quantity 1..99, fresh observation_id and unique request_id are required", nil
			}
			return farmReadCommand("crafting_execute", map[string]interface{}{
				"recipe": p.Recipe, "quantity": p.Quantity, "observation_id": p.Observation, "request_id": p.RequestID,
			})
		}),
	}
}
