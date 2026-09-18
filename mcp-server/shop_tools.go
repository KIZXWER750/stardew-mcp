package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"strings"
	"time"
)

const shopToolRules = `
SEED SHOPPING:
Only buy when the user authorized buying, with their quantity, total spending cap and retained money.
If budget is unspecified, explain a conservative proposed budget and ask the user before spending; read-only travel/inspection is allowed.
Use find_shop_route for observed loaded-map exits. Each link has approach candidates and an exit_id.
Move to a reachable approach then use_route_exit; verify location before the next link. Refresh route after transitions.
Do not guess map coordinates, shop hours or inventory. A graph route is not proof that a door is open.
Inside SeedShop, call open_pierre_shop. It walks to the fixed customer tile (4,19), faces north toward
Pierre's sales counter at (4,18), interacts once and verifies an actual ShopMenu. Never interact with
Pierre's NPC directly because that opens ordinary dialogue instead of the store. Then inspect_shop.
Select the exact seed item ID from inspect_shop and call buy_shop_item with its observation ID.
Buy at most the requested quantity. Never split requests to circumvent a budget or reserve.
A partial/uncertain transaction must stop and report spent gold, received seeds and cursor contents; do not retry it with a new observation or changed budget.
After a verified purchase close_shop. Return to Farm only if requested, using observed route links.
Purchase completion is not completion of later planting/watering; retain the chosen farm plot across travel.
Identical purchase parameters in this goal return the prior receipt; do not create multiple purchase goals unless explicitly requested.
Use the separate crop selling tools only if the user authorizes selling. No free items, money assignment, forced shop opening or teleporting.
Before a trip check get_shop_status. Normal trading hours are 09:00 inclusive to 17:00 exclusive, not building entry hours. Wednesday exceptions and festivals are returned from game observations. No automatic waiting, sleeping or time manipulation if closed.
`

type ShopEmptyParams struct{}

const pierreCounterX, pierreCounterY = 4, 18
const pierreStandX, pierreStandY = 4, 19

type ShopRouteParams struct {
	Destination string `json:"destination" jsonschema:"Observed destination name; SeedShop for Pierre or Farm for return"`
}
type ShopExitParams struct {
	ExitID string `json:"exit_id" jsonschema:"Exact observed exit_id from find_shop_route"`
}
type ShopBuyParams struct {
	ObservationID string `json:"observation_id"`
	ItemID        string `json:"item_id"`
	Quantity      int    `json:"quantity"`
	MaxTotalCost  int    `json:"max_total_cost"`
	ReserveMoney  int    `json:"reserve_money"`
}

func newShopScope() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b[:])
}
func (p ShopBuyParams) values(scope string) (map[string]interface{}, error) {
	if strings.TrimSpace(p.ObservationID) == "" || strings.TrimSpace(p.ItemID) == "" {
		return nil, fmt.Errorf("inspect_shop observation and exact item ID required")
	}
	if p.Quantity < 1 || p.Quantity > 99 || p.MaxTotalCost < 0 || p.MaxTotalCost > 2147483647 || p.ReserveMoney < 0 || p.ReserveMoney > 2147483647 {
		return nil, fmt.Errorf("quantity must be 1..99 and budgets nonnegative Int32 values")
	}
	key := sha256.Sum256([]byte(fmt.Sprintf("%s|%s|%d|%d|%d", scope, p.ItemID, p.Quantity, p.MaxTotalCost, p.ReserveMoney)))
	return map[string]interface{}{"request_id": hex.EncodeToString(key[:]), "observation_id": p.ObservationID, "item_id": p.ItemID, "quantity": p.Quantity, "max_total_cost": p.MaxTotalCost, "reserve_money": p.ReserveMoney}, nil
}
func (a *StardewAgent) buyShopItem(scope string, p ShopBuyParams) (string, error) {
	values, err := p.values(scope)
	if err != nil {
		return "TASK_BLOCKED: " + err.Error(), nil
	}
	a.toolMutex.Lock()
	defer a.toolMutex.Unlock()
	return farmReadCommand("shop_buy", values)
}

func (a *StardewAgent) openPierreShop() (string, error) {
	a.toolMutex.Lock()
	defer a.toolMutex.Unlock()
	state := gameClient.GetState()
	if state == nil {
		return "TASK_BLOCKED: game disconnected", nil
	}
	if state.Player.Location != "SeedShop" {
		return "TASK_BLOCKED: enter SeedShop before opening Pierre's counter", nil
	}
	if state.Player.ShopOpen {
		return "SHOP_OPENED: Pierre ShopMenu is already open; call inspect_shop", nil
	}
	if state.Player.X != pierreStandX || state.Player.Y != pierreStandY {
		a.doMoveTo(pierreStandX, pierreStandY)
		state = gameClient.GetState()
		if state == nil || state.Player.Location != "SeedShop" || state.Player.X != pierreStandX || state.Player.Y != pierreStandY {
			return fmt.Sprintf("TASK_BLOCKED: could not reach Pierre counter approach (%d,%d); actual state must be refreshed", pierreStandX, pierreStandY), nil
		}
	}
	response, err := gameClient.SendCommand("shop_open_pierre", nil)
	if err != nil {
		return "TASK_BLOCKED: Pierre counter interaction failed: " + err.Error(), nil
	}
	if response == nil || !response.Success {
		return "TASK_BLOCKED: Pierre counter interaction was rejected", nil
	}
	deadline := time.Now().Add(4 * time.Second)
	for time.Now().Before(deadline) {
		state = gameClient.GetState()
		if state != nil && state.Player.Location == "SeedShop" && state.Player.ShopOpen {
			return fmt.Sprintf("SHOP_OPENED: verified Pierre ShopMenu from stand=(%d,%d), counter=(%d,%d); call inspect_shop next", pierreStandX, pierreStandY, pierreCounterX, pierreCounterY), nil
		}
		time.Sleep(100 * time.Millisecond)
	}
	return fmt.Sprintf("TASK_BLOCKED: Pierre ShopMenu did not open after interacting north from (%d,%d) with counter (%d,%d); do not talk to Pierre NPC or repeat blindly", pierreStandX, pierreStandY, pierreCounterX, pierreCounterY), nil
}
