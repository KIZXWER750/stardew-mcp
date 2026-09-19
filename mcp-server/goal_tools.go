package main

import (
	"strings"

	copilot "github.com/github/copilot-sdk/go"
)

const goalToolRules = `
LONG-TERM GOALS:
Use create_long_term_goal only for a broad outcome the user intends to persist across days or game restarts. The current goal schema supports money_target only.
Interpret "reach/save N gold" as metric=current_balance. Use metric=balance_increase only when the user explicitly asks to gain N additional net gold from the starting balance.
Creating a goal does not authorize unlisted actions. Record only actions clearly within the user's request in authorized_actions; an empty list grants no gameplay action.
When the current user request explicitly permits returning home and sleeping after each day's work, persist that permission with allow_daily_return_home=true and allow_daily_sleep=true. For an already active matching goal, call set_goal_daily_life_policy. Never enable either policy from an old summary, an inference, or an automatic-resume prompt.
Goals persist and verify progress. Phase 4 can execute an authorized dated plan through leased, verified steps. Creating a goal alone does not start farming or earning money.
Use verify_long_term_goal to test completion from live game money. Never mark a money goal complete through text or a status tool.
Pause, resume or cancel a goal only when the user requested that state change; cancellation is terminal.
If essential information cannot be safely inferred, create a draft/active goal with the known scope and call request_goal_input once with one concise question and at most six options. The game will show a dedicated response window. After requesting input, stop this run and do not guess.
Do not ask through ordinary final chat when request_goal_input is available. A later continuation contains the saved answer; inspect the goal before continuing.
Never repeat an answered question. request_goal_input returns ALREADY_ANSWERED with the saved answer when the normalized question matches question history; reuse that answer and do not call request_goal_input again for the same issue. If more input is truly required, ask a materially different question that names the newly unresolved condition.
For every newly created broad money goal, call build_goal_plan even when the inventory sale inspection is empty. Existing live farm crops are a distinct strategy: tend_existing_crops permits watering and harvesting only already-planted crops, while farm_crops permits preparing and planting a new plot. If the user allows crop selling and forbids only seed purchases or new farming, record tend_existing_crops as within scope; never widen that to buy_seeds or farm_crops. Do not end in ordinary TASK_BLOCKED merely because the inventory is empty. Preserve an explicit prohibition unless the dedicated answer changes it.
Only after an answered saved question explicitly authorizes its named actions, use apply_goal_action_authorization with that question ID and the exact named actions, then refresh_goal_plan.
`

type GoalCreateParams struct {
	Summary              string   `json:"summary" jsonschema:"Concise user-visible goal summary"`
	Kind                 string   `json:"kind,omitempty" jsonschema:"Only money_target is currently supported"`
	Metric               string   `json:"metric,omitempty" jsonschema:"current_balance or balance_increase"`
	TargetValue          int      `json:"target_value" jsonschema:"Target gold value greater than zero"`
	ReserveMoney         int      `json:"reserve_money,omitempty" jsonschema:"Gold that later plans must preserve"`
	LatestWorkTime       int      `json:"latest_work_time,omitempty" jsonschema:"Latest allowed work time in HHMM, default 2200"`
	StrategyPreference   string   `json:"strategy_preference,omitempty" jsonschema:"balanced, fastest, highest_profit, low_risk or low_effort"`
	AuthorizedActions    []string `json:"authorized_actions,omitempty" jsonschema:"Gameplay action categories explicitly within user scope"`
	PreserveItemIDs      []string `json:"preserve_item_ids,omitempty" jsonschema:"Exact item IDs that later plans must protect"`
	DeadlineDayIndex     *int     `json:"deadline_day_index,omitempty" jsonschema:"Optional absolute DaysPlayed index when explicitly known"`
	DeadlineLabel        string   `json:"deadline_label,omitempty" jsonschema:"Human-readable deadline supplied by the user"`
	AllowDailyReturnHome bool     `json:"allow_daily_return_home,omitempty" jsonschema:"True only when the current user explicitly permits returning home after daily work"`
	AllowDailySleep      bool     `json:"allow_daily_sleep,omitempty" jsonschema:"True only when the current user explicitly permits sleeping to advance multi-day work"`
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

type GoalAuthorizationParams struct {
	GoalID     string   `json:"goal_id"`
	QuestionID string   `json:"question_id" jsonschema:"Answered question ID from inspect_long_term_goal"`
	Actions    []string `json:"actions" jsonschema:"Only exact actions named in that answered question: sell_crops, buy_seeds, tend_existing_crops, farm_crops"`
}

type GoalDailyLifePolicyParams struct {
	GoalID               string `json:"goal_id"`
	AllowDailyReturnHome bool   `json:"allow_daily_return_home" jsonschema:"Persist explicit permission to return home after daily work"`
	AllowDailySleep      bool   `json:"allow_daily_sleep" jsonschema:"Persist explicit permission to sleep and advance to the next planned day"`
}

func goalCommand(action string, values map[string]interface{}) (string, error) {
	return farmReadCommand(action, values)
}

func (a *StardewAgent) defineGoalTools() []copilot.Tool {
	return []copilot.Tool{
		copilot.DefineTool("create_long_term_goal", "Persist one broad outcome across game days and restarts. Supports money targets and verifies progress; economic tools can separately produce read-only strategy candidates.", func(p GoalCreateParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.Summary) == "" || p.TargetValue <= 0 {
				return "TASK_BLOCKED: summary and positive target_value required", nil
			}
			values := map[string]interface{}{"summary": p.Summary, "kind": p.Kind, "metric": p.Metric, "target_value": p.TargetValue,
				"reserve_money": p.ReserveMoney, "strategy_preference": p.StrategyPreference,
				"authorized_actions": p.AuthorizedActions, "preserve_item_ids": p.PreserveItemIDs, "deadline_label": p.DeadlineLabel,
				"allow_daily_return_home": p.AllowDailyReturnHome, "allow_daily_sleep": p.AllowDailySleep}
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
		copilot.DefineTool("resume_long_term_goal", "Resume a paused or blocked persistent goal. This changes status only and does not select or execute a profit strategy.", func(p GoalIDParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_status", map[string]interface{}{"goal_id": p.GoalID, "status": "active"})
		}),
		copilot.DefineTool("cancel_long_term_goal", "Permanently cancel one persistent goal. Cancelled goals cannot be resumed.", func(p GoalIDParams, _ copilot.ToolInvocation) (string, error) {
			return goalCommand("goal_status", map[string]interface{}{"goal_id": p.GoalID, "status": "cancelled"})
		}),
		copilot.DefineTool("request_goal_input", "Pause a persistent goal and show a dedicated in-game answer window for one essential new question. An answered equivalent is suppressed and returned as ALREADY_ANSWERED; reuse it and never ask it again. Stop the current run only when a new question was actually created.", func(p GoalInputParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.GoalID) == "" || strings.TrimSpace(p.Question) == "" || len(p.Options) > 6 {
				return "TASK_BLOCKED: goal_id, one question and at most six options required", nil
			}
			return goalCommand("goal_question", map[string]interface{}{"goal_id": p.GoalID, "question": p.Question, "options": p.Options})
		}),
		copilot.DefineTool("apply_goal_action_authorization", "After a dedicated goal question has a saved affirmative user answer, copy only the exact actions named by that question into the goal authorization list. Executes no gameplay action.", func(p GoalAuthorizationParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.GoalID) == "" || strings.TrimSpace(p.QuestionID) == "" || len(p.Actions) == 0 {
				return "TASK_BLOCKED: goal_id, answered question_id and actions required", nil
			}
			return goalCommand("goal_authorize_actions", map[string]interface{}{"goal_id": p.GoalID, "question_id": p.QuestionID, "actions": p.Actions})
		}),
		copilot.DefineTool("set_goal_daily_life_policy", "Persist or revoke explicit current-user permission for an existing long-term goal to return home and sleep after each completed day. Executes no gameplay action. Never infer permission during an automatic resume.", func(p GoalDailyLifePolicyParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.GoalID) == "" {
				return "TASK_BLOCKED: goal_id required", nil
			}
			return goalCommand("goal_daily_life_policy", map[string]interface{}{"goal_id": p.GoalID,
				"allow_daily_return_home": p.AllowDailyReturnHome, "allow_daily_sleep": p.AllowDailySleep})
		}),
	}
}
