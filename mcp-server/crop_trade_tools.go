package main

import (
	"fmt"
	"strings"
)

const cropTradeRules = `
CROP SALES AND CHESTS:
Sales are optional, only when authorized by the user's goal. Never sell food, gifts, quest items, or crops the user said to keep.
Before leaving for Pierre, get_shop_status. If closed/unknown report why and stop that trading stage; do not repeatedly try doors or sleep without authorization.
inspect_sellable_crops observes inventory candidate stacks. It does not guarantee the shop accepts them.
If the goal authorizes crops from chests, inspect_storage to locate ordinary nearby chests, move cardinally adjacent, open_storage once, and inspect_storage to verify actual contents.
Take only approved crop-stack quote IDs using take_storage_crop. Keep_in_chest applies per item ID across qualities in that chest.
Always close_storage when done inspecting or taking crops, before reporting completion or walking away. Refresh inventory observations after withdrawal, before selling.
All sales/withdrawals are WHOLE STACK only. If a stack would violate a retained quantity, skip and report it; do not lower the reserve or split it through other tools.
Sell only at the live Pierre ShopMenu, using sell_crop_stack with inventory quote and keep_quantity across qualities. Minimum_total_price is a floor, not guaranteed market value.
After each result, account for actual received gold and sold quantity. On uncertain results or cursor items, stop, report and do not reissue with different limits.
Sales and transfers use repeat protection per goal. Do not replay completed stacks or perform unrequested sales to fund a purchase.
For sell-then-buy goals, sell first, refresh money and catalog, then buy only within the user's total spending and reserve limits.
Finally close_shop; return to Farm only if requested. Do not put items into the shipping bin, sell seeds or resources, or discard anything.
`

type CropSaleParams struct {
	QuoteID           string `json:"quote_id"`
	KeepQuantity      int    `json:"keep_quantity"`
	MinimumTotalPrice int    `json:"minimum_total_price"`
}
type StorageOpenParams struct {
	ChestID string `json:"chest_id"`
}
type StorageTakeParams struct {
	QuoteID     string `json:"quote_id"`
	KeepInChest int    `json:"keep_in_chest"`
}

func (p CropSaleParams) values() (map[string]interface{}, error) {
	if strings.TrimSpace(p.QuoteID) == "" || p.KeepQuantity < 0 || p.KeepQuantity > 2147483647 || p.MinimumTotalPrice < 1 || p.MinimumTotalPrice > 2147483647 {
		return nil, fmt.Errorf("observed quote, nonnegative Int32 reserve and positive Int32 minimum price required")
	}
	return map[string]interface{}{"quote_id": p.QuoteID, "keep_quantity": p.KeepQuantity, "minimum_total_price": p.MinimumTotalPrice}, nil
}
func (p StorageTakeParams) values() (map[string]interface{}, error) {
	if strings.TrimSpace(p.QuoteID) == "" || p.KeepInChest < 0 || p.KeepInChest > 2147483647 {
		return nil, fmt.Errorf("observed chest quote and nonnegative Int32 reserve required")
	}
	return map[string]interface{}{"quote_id": p.QuoteID, "keep_in_chest": p.KeepInChest}, nil
}
