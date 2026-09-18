package main

import (
	"encoding/json"
	"fmt"
)

type AnalyzeParams struct {
	PlotParams
	Operation string `json:"operation" jsonschema:"clear, till, prepare, plant, water, or harvest"`
}
type WaterSourceParams struct {
	SearchRadius int `json:"search_radius,omitempty" jsonschema:"Search radius around player: 1..64, default 32"`
	MaxSources   int `json:"max_sources,omitempty" jsonschema:"Maximum candidates: 1..20, default 8"`
}
type RefillParams struct {
	Location      string `json:"location" jsonschema:"Farm"`
	X             int    `json:"x" jsonschema:"Water source X returned by find_water_sources"`
	Y             int    `json:"y" jsonschema:"Water source Y returned by find_water_sources"`
	MinimumEnergy int    `json:"minimum_energy,omitempty"`
	StopTime      int    `json:"stop_time,omitempty"`
}

func (p AnalyzeParams) valuesForAnalysis() (map[string]interface{}, error) {
	if err := p.validate(); err != nil {
		return nil, err
	}
	switch p.Operation {
	case "clear", "till", "prepare", "plant", "water", "harvest":
	default:
		return nil, fmt.Errorf("unsupported operation")
	}
	if p.Operation == "plant" && p.SeedItemID == "" {
		return nil, fmt.Errorf("seed_item_id required for plant analysis")
	}
	return p.values(p.Operation), nil
}
func (p WaterSourceParams) values() (map[string]interface{}, error) {
	r, n := p.SearchRadius, p.MaxSources
	if r == 0 {
		r = 32
	}
	if n == 0 {
		n = 8
	}
	if r < 1 || r > 64 || n < 1 || n > 20 {
		return nil, fmt.Errorf("search_radius must be 1..64 and max_sources 1..20")
	}
	return map[string]interface{}{"search_radius": r, "max_sources": n}, nil
}
func farmReadCommand(action string, values map[string]interface{}) (string, error) {
	r, err := gameClient.SendCommand(action, values)
	if err != nil {
		return "TASK_BLOCKED: " + err.Error(), nil
	}
	if r == nil {
		return "TASK_BLOCKED: missing observation", nil
	}
	if !r.Success {
		return "TASK_BLOCKED: " + r.Message, nil
	}
	b, err := json.Marshal(r.Data)
	return string(b), err
}
func (a *StardewAgent) analyzeFarm(p AnalyzeParams) (string, error) {
	v, e := p.valuesForAnalysis()
	if e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	return farmReadCommand("farm_analyze", v)
}
func (a *StardewAgent) findWaterSources(p WaterSourceParams) (string, error) {
	v, e := p.values()
	if e != nil {
		return "TASK_BLOCKED: " + e.Error(), nil
	}
	return farmReadCommand("farm_water_sources", v)
}
func (a *StardewAgent) refillCan(p RefillParams) (string, error) {
	return a.runFarmArea("refill", PlotParams{Location: p.Location, X: p.X, Y: p.Y, Width: 1, Height: 1, MinimumEnergy: p.MinimumEnergy, StopTime: p.StopTime})
}
func (a *StardewAgent) refillKey(key string) string {
	a.requestMu.Lock()
	defer a.requestMu.Unlock()
	return fmt.Sprintf("%s:water-generation=%d", key, a.farmWaterGeneration)
}
