package main

import (
	"os"
	"strings"
	"testing"
)

func TestStorageImplementationContract(t *testing.T) {
	b, err := os.ReadFile("../mod/StardewMCP/CropTrading.cs")
	if err != nil {
		t.Fatal(err)
	}
	s := string(b)
	for _, required := range []string{
		"SafeStorageItem", "StorageSummary", "totalItemUnits", "occupiedSlots", "freeSlots",
		"itemTotals", "qualityTotals", "StorageCanStack", "StorageDestinationSlots",
		"maximumStackSize", "TakeStorageItem", "StoreInventoryItem", "TRANSFER_AND_ORGANIZE_VERIFIED",
		"StorageMenuItems", "fillStacksButton", "StackInventoryToStorage", "ADD_TO_EXISTING_STACKS_AND_ORGANIZE_VERIFIED",
		"organizeButton", "OrganizeStorageMenu", "OrganizeStorage", "TRANSFER_AND_ORGANIZE_VERIFIED", "ORGANIZE_TOTAL_MISMATCH",
		"chest.addItem(moving)", "q.Item.getOne()", "CONSERVATION_CHECK_FAILED", "NATIVE_CHEST_ADD_AND_ORGANIZE_VERIFIED", "PARTIAL_ACCEPT_PRESERVED",
		"Game1.exitActiveMenu()", "STORAGE_MENU_CLOSED", "MENU_CLOSE_NOT_CONFIRMED",
		"InspectClosedStorage", "ClosedStorageCapacity", "GetActualCapacity", "ReadOnlyStorageRow", "OBSERVED_READ_ONLY",
	} {
		if !strings.Contains(s, required) {
			t.Fatal("storage contract missing: " + required)
		}
	}
}

func TestClosedStorageInspectionIsReadOnly(t *testing.T) {
	b, err := os.ReadFile("../mod/StardewMCP/CropTrading.cs")
	if err != nil {
		t.Fatal(err)
	}
	s := string(b)
	start := strings.Index(s, "private CommandResponse InspectClosedStorage")
	if start < 0 {
		t.Fatal("closed storage inspector start missing")
	}
	end := strings.Index(s[start:], "private CommandResponse OpenStorage")
	if end < 0 {
		t.Fatal("closed storage inspector end missing")
	}
	body := s[start : start+end]
	for _, forbidden := range []string{"QuoteCrop(", "addItem(", "receiveLeftClick(", "exitActiveMenu(", "_chestObservations", "Game1.player.Items["} {
		if strings.Contains(body, forbidden) {
			t.Fatal("closed storage inspector mutates or exposes handles: " + forbidden)
		}
	}
	for _, required := range []string{"chest.Items", "StorageSummary(items)", "ReadOnlyStorageRow", "OBSERVED_READ_ONLY"} {
		if !strings.Contains(body, required) {
			t.Fatal("closed storage inspector missing read-only data: " + required)
		}
	}
}
