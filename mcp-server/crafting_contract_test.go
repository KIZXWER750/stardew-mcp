package main

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

func TestCraftingRulesPreferKnowledgeAndDedicatedQuestions(t *testing.T) {
	for _, required := range []string{"inspect_crafting_recipe", "lookup_game_knowledge", "type=materials", "request_player_input", "Re-inspect", "request ID"} {
		if !strings.Contains(craftingToolRules, required) {
			t.Fatalf("crafting rule missing %q", required)
		}
	}
}

func TestMaterialKnowledgeHasRankedAcquisitionMethods(t *testing.T) {
	data, err := os.ReadFile("../mod/StardewMCP/data/knowledge/wiki/en/materials.json")
	if err != nil {
		t.Fatal(err)
	}
	var document struct {
		Category string `json:"category"`
		Entries  []struct {
			Subject string `json:"subject"`
			Facts   struct {
				Methods []struct {
					Rank int `json:"rank"`
				} `json:"acquisitionMethods"`
			} `json:"facts"`
			Source struct {
				URL string `json:"pageUrl"`
			} `json:"source"`
		} `json:"entries"`
	}
	if err := json.Unmarshal(data, &document); err != nil {
		t.Fatal(err)
	}
	if document.Category != "materials" || len(document.Entries) < 8 {
		t.Fatal("material knowledge category or entry count is incomplete")
	}
	for _, entry := range document.Entries {
		if entry.Subject == "" || entry.Source.URL == "" || len(entry.Facts.Methods) == 0 || entry.Facts.Methods[0].Rank != 1 {
			t.Fatalf("material entry lacks ranked methods or provenance: %s", entry.Subject)
		}
	}
}

func TestAICallCounterMarker(t *testing.T) {
	if !isAICallLog("2026/09/20 [AI API CALL] provider=openai") || isAICallLog("[AI USAGE]") {
		t.Fatal("AI call marker detection is not exact enough")
	}
}

func TestCraftingToolSchemasExposeObservationAndRequestIDs(t *testing.T) {
	a := &StardewAgent{}
	config := a.toolSessionConfig()
	byName := map[string]bool{}
	for _, name := range config.AvailableTools {
		byName[name] = true
	}
	if !byName["inspect_crafting_recipe"] || !byName["craft_item"] {
		t.Fatal("normal crafting tools are not available")
	}
}
