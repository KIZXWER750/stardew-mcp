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
Use observed crop data and inspect_sellable_crops estimates for planning. Never make a trip to Pierre merely to sell one sample and discover its price. Harvest every ready crop in the authorized plot first, consolidate the inventory, then make one trading trip. While Pierre's menu is open, sell every authorized target crop/quality stack needed by the goal before leaving; refresh quotes in place after each whole-stack sale.
For chest work, inspect_storage locates ordinary nearby chests. Move cardinally adjacent, open_storage once, then inspect_storage again to read exact unit counts, occupied/free slots, item totals and quality totals.
inspect_closed_storage is a read-only survey of every ordinary player chest in the current loaded location. It may reveal position, color, item rows, quantities and occupied/free capacity, but returns no quote IDs or mutation handles. Never treat its rows as authorization to transfer: approach, open and inspect_storage again first.
Use only fresh quote IDs returned for the actually open chest. take_storage_item moves one observed whole stack to inventory. store_inventory_item uses the native Chest.addItem path and removes only the accepted quantity from inventory, with combined-unit conservation verification. Reserve quantities apply per item ID across qualities.
stack_inventory_to_storage presses the open chest menu's native Add To Existing Stacks button once. It moves only inventory items compatible with stacks already present in that chest and verifies the total unit delta.
Transfers first merge into compatible partial stacks, then use empty slots. Always inspect_storage again after a transfer and close_storage before reporting completion or walking away.
Every successful chest mutation is followed by the chest menu's native Organize button and a before/after total-unit conservation check. organize_storage may also be called explicitly. close_storage does not click Organize again in the same tick; it uses the game's global active-menu exit after confirming an empty cursor.
All sales and chest transfers are WHOLE STACK only. If a stack would violate a retained quantity, skip and report it; do not lower the reserve or split it through other tools.
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
type StoragePutParams struct {
	QuoteID         string `json:"quote_id"`
	KeepInInventory int    `json:"keep_in_inventory"`
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
func (p StoragePutParams) values() (map[string]interface{}, error) {
	if strings.TrimSpace(p.QuoteID) == "" || p.KeepInInventory < 0 || p.KeepInInventory > 2147483647 {
		return nil, fmt.Errorf("observed inventory quote and nonnegative Int32 reserve required")
	}
	return map[string]interface{}{"quote_id": p.QuoteID, "keep_in_inventory": p.KeepInInventory}, nil
}
