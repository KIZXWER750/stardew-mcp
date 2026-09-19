package main

import (
	"strings"

	copilot "github.com/github/copilot-sdk/go"
)

const memoryToolRules = `
LONG-TERM MEMORY AND GAME KNOWLEDGE:
Memory is save-specific. Chest contents are last-observed snapshots and must not be presented as current without a fresh inspection.
Game knowledge combines installed game/mod extraction with an attributed Stardew Valley Wiki fact cache. Current game state, live menus, conditions and passability remain final authority. Wiki results include their page URL, revision and verification status and must not override installed or live data.
Use search_memory and get_chest_memory before relying on prior chest purposes, unfinished work or notes.
Only call set_chest_purpose or remember_note when the current user request provides or authorizes the information. Set confirmed_by_user=false for an inference.
Persistent tasks must describe a concrete unfinished intention. Never create a task merely to restate completed work, tool output or speculative advice.
Completing a persistent task is irreversible through the tool interface; call complete_persistent_task only after its outcome is verified.
lookup_game_knowledge and find_world_route are read-only. A cached route does not authorize travel and does not prove that a door is currently open.
`

type MemorySearchParams struct {
	Query    string `json:"query,omitempty" jsonschema:"Optional text matched against IDs, purposes, item names, notes and task summaries"`
	Category string `json:"category,omitempty" jsonschema:"Optional chest, note or task filter"`
	Location string `json:"location,omitempty" jsonschema:"Optional internal game location filter"`
	Status   string `json:"status,omitempty" jsonschema:"Optional persistent task status filter"`
}
type ChestMemoryParams struct {
	MemoryID string `json:"memory_id" jsonschema:"Stable memoryId returned by chest inspection or memory search"`
}
type ChestPurposeParams struct {
	MemoryID        string `json:"memory_id"`
	Purpose         string `json:"purpose"`
	ConfirmedByUser bool   `json:"confirmed_by_user" jsonschema:"True only when the user explicitly supplied or confirmed this purpose"`
}
type RememberNoteParams struct {
	Text            string `json:"text"`
	Kind            string `json:"kind,omitempty" jsonschema:"Short structured category such as preference, plan or reminder"`
	ConfirmedByUser bool   `json:"confirmed_by_user" jsonschema:"True only for information explicitly supplied or confirmed by the user"`
}
type TaskListParams struct {
	Status string `json:"status,omitempty" jsonschema:"Optional pending, active, paused, blocked, completed or cancelled"`
}
type TaskUpsertParams struct {
	TaskID          string `json:"task_id,omitempty" jsonschema:"Existing task ID to update; omit to create"`
	Kind            string `json:"kind,omitempty" jsonschema:"Required when creating a task"`
	Summary         string `json:"summary,omitempty" jsonschema:"Required when creating a task"`
	Status          string `json:"status,omitempty" jsonschema:"pending, active, paused, blocked, completed or cancelled"`
	Priority        int    `json:"priority,omitempty"`
	Location        string `json:"location,omitempty"`
	X               *int   `json:"x,omitempty"`
	Y               *int   `json:"y,omitempty"`
	TargetType      string `json:"target_type,omitempty"`
	ResumeMode      string `json:"resume_mode,omitempty" jsonschema:"manual, automatic, next_day or when_condition"`
	MinimumEnergy   int    `json:"minimum_energy,omitempty"`
	LatestStartTime int    `json:"latest_start_time,omitempty" jsonschema:"Game HHMM; 600..2600"`
}
type PersistentTaskIDParams struct {
	TaskID string `json:"task_id"`
}
type KnowledgeLookupParams struct {
	Subject string `json:"subject,omitempty" jsonschema:"Location, route, shop, crop, fish, villager, festival, recipe, bundle or fact text to find"`
	Type    string `json:"type,omitempty" jsonschema:"Optional location, route, shop, wiki, crops, fish, villagers, festivals, crafting or bundles"`
}
type WorldRouteParams struct {
	From string `json:"from,omitempty" jsonschema:"Internal start location; defaults to current location"`
	To   string `json:"to" jsonschema:"Internal destination location"`
}

func memoryRead(action string, values map[string]interface{}) (string, error) {
	return farmReadCommand(action, values)
}

func relevantMemoryContext(goal, location string) string {
	result, err := farmReadCommand("memory_context", map[string]interface{}{"goal": goal, "location": location})
	if err != nil || strings.HasPrefix(result, "TASK_BLOCKED:") {
		return ""
	}
	const maxContextBytes = 16000
	if len(result) > maxContextBytes {
		result = result[:maxContextBytes] + "\n[SELECTED MEMORY TRUNCATED AT 16000 BYTES]"
	}
	return result
}

func (a *StardewAgent) defineMemoryTools() []copilot.Tool {
	return []copilot.Tool{
		copilot.DefineTool("search_memory", "Search save-specific remembered chests, notes and persistent tasks. Read-only; chest contents include observation timestamps and may be stale.", func(p MemorySearchParams, _ copilot.ToolInvocation) (string, error) {
			return memoryRead("memory_search", map[string]interface{}{"query": p.Query, "category": p.Category, "location": p.Location, "status": p.Status})
		}),
		copilot.DefineTool("get_chest_memory", "Read one remembered chest by stable memory_id, including purpose, position, color and last-observed contents. No live chest access.", func(p ChestMemoryParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.MemoryID) == "" {
				return "TASK_BLOCKED: memory_id required", nil
			}
			return memoryRead("memory_chest_get", map[string]interface{}{"memory_id": p.MemoryID})
		}),
		copilot.DefineTool("set_chest_purpose", "Save a concise purpose for a remembered chest. Use confirmed_by_user=true only for a purpose explicitly supplied or confirmed by the user.", func(p ChestPurposeParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.MemoryID) == "" || strings.TrimSpace(p.Purpose) == "" || len(p.Purpose) > 500 {
				return "TASK_BLOCKED: valid memory_id and purpose of 1..500 characters required", nil
			}
			return memoryRead("memory_chest_purpose", map[string]interface{}{"memory_id": p.MemoryID, "purpose": p.Purpose, "confirmed_by_user": p.ConfirmedByUser})
		}),
		copilot.DefineTool("remember_note", "Store a save-specific structured note only when the current user request provides or authorizes the information.", func(p RememberNoteParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.Text) == "" || len(p.Text) > 1000 {
				return "TASK_BLOCKED: note text must be 1..1000 characters", nil
			}
			return memoryRead("memory_note_upsert", map[string]interface{}{"text": p.Text, "kind": p.Kind, "confirmed_by_user": p.ConfirmedByUser})
		}),
		copilot.DefineTool("list_persistent_tasks", "List save-specific unfinished or historical tasks and their structured resume conditions. Read-only.", func(p TaskListParams, _ copilot.ToolInvocation) (string, error) {
			return memoryRead("memory_task_list", map[string]interface{}{"status": p.Status})
		}),
		copilot.DefineTool("upsert_persistent_task", "Create or update a concrete save-specific task. New tasks require kind and summary. Existing terminal tasks cannot be reactivated.", func(p TaskUpsertParams, _ copilot.ToolInvocation) (string, error) {
			if p.TaskID == "" && (strings.TrimSpace(p.Kind) == "" || strings.TrimSpace(p.Summary) == "") {
				return "TASK_BLOCKED: new tasks require kind and summary", nil
			}
			if p.LatestStartTime != 0 && (p.LatestStartTime < 600 || p.LatestStartTime > 2600) {
				return "TASK_BLOCKED: latest_start_time must be 600..2600", nil
			}
			v := map[string]interface{}{}
			if p.TaskID != "" {
				v["task_id"] = p.TaskID
			}
			if p.Kind != "" {
				v["kind"] = p.Kind
			}
			if p.Summary != "" {
				v["summary"] = p.Summary
			}
			if p.Status != "" {
				v["status"] = p.Status
			}
			if p.Priority != 0 {
				v["priority"] = p.Priority
			}
			if p.ResumeMode != "" {
				v["resume_mode"] = p.ResumeMode
			}
			if p.MinimumEnergy != 0 {
				v["minimum_energy"] = p.MinimumEnergy
			}
			if p.LatestStartTime != 0 {
				v["latest_start_time"] = p.LatestStartTime
			}
			if p.Location != "" {
				v["location"], v["target_type"] = p.Location, p.TargetType
				if p.X != nil {
					v["x"] = *p.X
				}
				if p.Y != nil {
					v["y"] = *p.Y
				}
			}
			return memoryRead("memory_task_upsert", v)
		}),
		copilot.DefineTool("complete_persistent_task", "Mark one persistent task completed after its result has been verified. Terminal tasks cannot be reopened through memory tools.", func(p PersistentTaskIDParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.TaskID) == "" {
				return "TASK_BLOCKED: task_id required", nil
			}
			return memoryRead("memory_task_complete", map[string]interface{}{"task_id": p.TaskID})
		}),
		copilot.DefineTool("lookup_game_knowledge", "Search installed-content knowledge plus attributed wiki facts about crops, fish, villagers, festivals, shops, crafting and bundles. Use type=wiki or a category to browse it. Read-only; installed and live state are final authority.", func(p KnowledgeLookupParams, _ copilot.ToolInvocation) (string, error) {
			return memoryRead("knowledge_lookup", map[string]interface{}{"subject": p.Subject, "type": p.Type})
		}),
		copilot.DefineTool("find_world_route", "Find a read-only cached location-to-location route from installed map data. It does not move or prove current access.", func(p WorldRouteParams, _ copilot.ToolInvocation) (string, error) {
			if strings.TrimSpace(p.To) == "" {
				return "TASK_BLOCKED: destination required", nil
			}
			return memoryRead("knowledge_route", map[string]interface{}{"from": p.From, "to": p.To})
		}),
	}
}
