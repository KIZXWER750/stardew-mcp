package main

import (
	"strings"

	copilot "github.com/github/copilot-sdk/go"
)

const goalToolRules = `
LONG-TERM GOALS (PHASE 1):
Use create_long_term_goal only for a broad outcome the user intends to persist across days or game restarts. Phase 1 supports money_target only.
Interpret "reach/save N gold" as metric=current_balance. Use metric=balance_increase only when the user explicitly asks to gain N additional net gold from the starting balance.
Creating a goal does not authorize unlisted actions. Record only actions clearly within the user's request in authorized_actions; an empty list grants no gameplay action.
Phase 1 persists and verifies goals but does not autonomously plan or execute profit strategies. Never claim that creating a goal started farming or earning money.
Use verify_long_term_goal to test completion from live game money. Never mark a money goal complete through text or a status tool.
Pause, resume or cancel a goal only when the user requested that state change; cancellation is terminal.
If essential information cannot be safely inferred, create a draft/active goal with the known scope and call request_goal_input once with one concise question and at most six options. The game will show a dedicated response window. After requesting input, stop this run and do not guess.
Do not ask through ordinary final chat when request_goal_input is available. A later continuation contains the saved answer; inspect the goal before continuing.
`

type GoalCreateParams struct {
	Summary            string   `json:"summary" jsonschema:"Concise user-visible goal summary"`
	Kind               string   `json:"kind,omitempty" jsonschema:"Only money_target is supported in Phase 1"`
	Metric             string   `json:"metric,omitempty" jsonschema:"current_balance or balance_increase"`
	TargetValue        int      `json:"target_value" jsonschema:"Target gold value greater than zero"`
	ReserveMoney       int      `json:"reserve_money,omitempty" jsonschema:"Gold that later plans must preserve"`
	LatestWorkTime     int      `json:"latest_work_time,omitempty" jsonschema:"Latest allowed work time in HHMM, default 2200"`
	StrategyPreference string   `json:"strategy_preference,omitempty" jsonschema:"balanced, fastest, highest_profit, low_risk or low_effort"`
	AuthorizedActions  []string `json:"authorized_actions,omitempty" jsonschema:"Gameplay action categories explicitly within user scope"`
	PreserveItemIDs    []string `json:"preserve_item_ids,omitempty" jsonschema:"Exact item IDs that later plans must protect"`
	DeadlineDayIndex   *int     `json:"deadline_day_index,omitempty" jsonschema:"Optional absolute DaysPlayed index when explicitly known"`
	DeadlineLabel      string   `json:"deadline_label,omitempty" jsonschema:"Human-readable deadline supplied by the user"`
}

type GoalListParams struct {
	Status string `json:"status,omitempty" jsonschema:"Optional goal status filter"`
}

type GoalIDParams struct {
	GoalID string `json:"goal_id"`
}

type GoalInputParams struct {
	GoalID   string   `json:"goal_id"`
	Question string   `json:"question" jsonschema:"One concise question essential to continue"`
	Options  []string `json:"options,omitempty" jsonschema:"Zero to six short suggested answers"`
}

func goalCommand(action string, values map[string]interface{}) (string, error) {
	return farmReadCommand(action, values)
}

func (a *StardewAgent) defineGoalTools() []copilot.Tool {
	return []copilot.Tool{
		copilot.DefineTool("create_long_term_goal", "Persist one broad outcome across game days and restarts. Phase 1 supports money targets and verifies progress, but does not create or execute a profit plan.", func(p GoalCreateParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.Summary) == "" || p.TargetValue <= 0 {
				return "TASK_BLOCKED: summary and positive target_value required", nil
			}
			values := map[string]interface{}{"summary": p.Summary, "kind": p.Kind, "metric": p.Metric, "target_value": p.TargetValue,
				"reserve_money": p.ReserveMoney, "strategy_preference": p.StrategyPreference,
				"authorized_actions": p.AuthorizedActions, "preserve_item_ids": p.PreserveItemIDs, "deadline_label": p.DeadlineLabel}
			if p.LatestWorkTime != 0 {
				values["latest_work_time"] = p.LatestWorkTime
			}
			if p.DeadlineDayIndex != nil {
				values["deadline_day_index"] = *p.DeadlineDayIndex
			}
			return goalCommand("goal_create", values)
		}),
		copilot.DefineTool("list_long_term_goals", "List persistent broad goals and their verified progress. Read-only.", func(p GoalListParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_list", map[string]interface{}{"status": p.Status})
		}),
		copilot.DefineTool("inspect_long_term_goal", "Read one persistent goal, constraints, question history and live-money progress. Read-only.", func(p GoalIDParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_inspect", map[string]interface{}{"goal_id": p.GoalID})
		}),
		copilot.DefineTool("verify_long_term_goal", "Re-evaluate one money goal against live game money and return verified progress. Completion is automatic when the predicate is met.", func(p GoalIDParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_verify", map[string]interface{}{"goal_id": p.GoalID})
		}),
		copilot.DefineTool("pause_long_term_goal", "Pause an active persistent goal without deleting its progress.", func(p GoalIDParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_status", map[string]interface{}{"goal_id": p.GoalID, "status": "paused"})
		}),
		copilot.DefineTool("resume_long_term_goal", "Resume a paused or blocked persistent goal. This changes status only; Phase 1 does not auto-plan profit actions.", func(p GoalIDParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_status", map[string]interface{}{"goal_id": p.GoalID, "status": "active"})
		}),
		copilot.DefineTool("cancel_long_term_goal", "Permanently cancel one persistent goal. Cancelled goals cannot be resumed.", func(p GoalIDParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_status", map[string]interface{}{"goal_id": p.GoalID, "status": "cancelled"})
		}),
		copilot.DefineTool("request_goal_input", "Pause a persistent goal and show a dedicated in-game answer window for one essential question. Stop the current run after this call.", func(p GoalInputParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.GoalID) == "" || strings.TrimSpace(p.Question) == "" || len(p.Options) > 6 {
				return "TASK_BLOCKED: goal_id, one question and at most six options required", nil
			}
			return goalCommand("goal_question", map[string]interface{}{"goal_id": p.GoalID, "question": p.Question, "options": p.Options})
		}),
	}
}
