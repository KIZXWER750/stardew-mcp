package main

import (
	"fmt"
	"strings"

	copilot "github.com/github/copilot-sdk/go"
)

const goalExecutionRules = `
PERSISTENT GOAL PLAN EXECUTION (PHASE 4):
After build_goal_plan or refresh_goal_plan returns PLANNED, call start_goal_plan_execution. For a resumed automatic run call continue_goal_plan_execution.
The execution response leases exactly one step. Execute only that step and its safe prerequisites using normal gameplay tools, then call report_goal_plan_step with the exact step_id and lease_id.
Never report completed from intent, an input attempt, or prose. Use completed only after the underlying tool returned a verified success. Use paused for time, energy, closed-shop, empty-can, or another recoverable condition. Use blocked only after safe recovery inside the authorized scope is exhausted.
For select_farm_plot, call find_plot_candidates using the exact requested dimensions, choose only a returned reachable candidate, and pass it to bind_goal_plan_plot. That plot remains fixed across days and restarts.
Keep calling the returned next directive in the same run until it says WAITING, BLOCKED, REPLAN_REQUIRED, or GOAL_COMPLETED. On REPLAN_REQUIRED call refresh_goal_plan and start execution again if the goal remains active.
WAITING means stop this run. The in-game host automatically resumes a due persisted step on a later day. Do not invent sleep authorization or advance time merely to reach a future step.
Plan execution does not broaden authorization. Preserve reserve money, protected items, fixed plot bounds, latest work time, and normal gameplay verification. F7 pauses the persistent goal and its plan.
`

type GoalExecutionParams struct {
	GoalID string `json:"goal_id" jsonschema:"Exact persistent goal ID"`
}

type GoalPlotBindParams struct {
	GoalID   string `json:"goal_id"`
	StepID   string `json:"step_id"`
	LeaseID  string `json:"lease_id"`
	Location string `json:"location" jsonschema:"Must be Farm"`
	X        int    `json:"x"`
	Y        int    `json:"y"`
	Width    int    `json:"width"`
	Height   int    `json:"height"`
}

type GoalStepReportParams struct {
	GoalID        string `json:"goal_id"`
	StepID        string `json:"step_id"`
	LeaseID       string `json:"lease_id"`
	Outcome       string `json:"outcome" jsonschema:"completed, skipped, paused, or blocked"`
	ResultSummary string `json:"result_summary" jsonschema:"Concise exact verified tool result or blocking evidence"`
}

func goalExecutionValues(p GoalExecutionParams) (map[string]interface{}, error) {
	if strings.TrimSpace(p.GoalID) == "" {
		return nil, fmt.Errorf("goal_id required")
	}
	return map[string]interface{}{"goal_id": p.GoalID}, nil
}

func (a *StardewAgent) defineGoalExecutionTools() []copilot.Tool {
	call := func(action string, p GoalExecutionParams) (string, error) {
		v, err := goalExecutionValues(p)
		if err != nil {
			return "TASK_BLOCKED: " + err.Error(), nil
		}
		return farmReadCommand(action, v)
	}
	return []copilot.Tool{
		copilot.DefineTool("start_goal_plan_execution", "Start a fresh persisted plan after verifying it is current and authorized. Leases one due step at a time; does not itself perform that step.", func(p GoalExecutionParams, _ copilot.ToolInvocation) (string, error) {
			return call("goal_execution_start", p)
		}),
		copilot.DefineTool("continue_goal_plan_execution", "Resume an executing or waiting persisted plan and obtain its exact due leased step. Safe across day changes and game restarts.", func(p GoalExecutionParams, _ copilot.ToolInvocation) (string, error) {
			return call("goal_execution_continue", p)
		}),
		copilot.DefineTool("bind_goal_plan_plot", "Bind one reachable candidate returned by find_plot_candidates to the leased select_farm_plot step. The fixed rectangle is reused for all later farm steps.", func(p GoalPlotBindParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.GoalID) == "" || strings.TrimSpace(p.StepID) == "" || strings.TrimSpace(p.LeaseID) == "" || p.Location != "Farm" || p.X < 0 || p.Y < 0 || p.Width < 1 || p.Height < 1 || p.Width*p.Height > 64 {
				return "TASK_BLOCKED: valid goal, lease, Farm rectangle and 1..64 tiles required", nil
			}
			return farmReadCommand("goal_execution_bind_plot", map[string]interface{}{"goal_id": p.GoalID, "step_id": p.StepID, "lease_id": p.LeaseID, "location": p.Location, "x": p.X, "y": p.Y, "width": p.Width, "height": p.Height})
		}),
		copilot.DefineTool("report_goal_plan_step", "Report one leased step only after its normal gameplay result. The mod independently checks important money, inventory, crop, soil and watering changes before advancing.", func(p GoalStepReportParams, _ copilot.ToolInvocation) (string, error) {
			outcome := strings.ToLower(strings.TrimSpace(p.Outcome))
			if strings.TrimSpace(p.GoalID) == "" || strings.TrimSpace(p.StepID) == "" || strings.TrimSpace(p.LeaseID) == "" || strings.TrimSpace(p.ResultSummary) == "" || (outcome != "completed" && outcome != "skipped" && outcome != "paused" && outcome != "blocked") {
				return "TASK_BLOCKED: exact goal/step/lease, valid outcome and result_summary required", nil
			}
			return farmReadCommand("goal_execution_report", map[string]interface{}{"goal_id": p.GoalID, "step_id": p.StepID, "lease_id": p.LeaseID, "outcome": outcome, "result_summary": p.ResultSummary})
		}),
	}
}
