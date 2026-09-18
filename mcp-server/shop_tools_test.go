package main

import "testing"

func TestShopPurchaseValidation(t *testing.T) {
	good := ShopBuyParams{ObservationID: "observed", ItemID: "(O)472", Quantity: 15, MaxTotalCost: 500, ReserveMoney: 100}
	if _, err := good.values("run"); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*ShopBuyParams){func(p *ShopBuyParams) { p.ObservationID = "" }, func(p *ShopBuyParams) { p.ItemID = "" }, func(p *ShopBuyParams) { p.Quantity = 0 }, func(p *ShopBuyParams) { p.Quantity = 100 }, func(p *ShopBuyParams) { p.MaxTotalCost = -1 }, func(p *ShopBuyParams) { p.ReserveMoney = -1 }} {
		p := good
		change(&p)
		if _, err := p.values("run"); err == nil {
			t.Errorf("accepted invalid purchase: %+v", p)
		}
	}
}
func TestShopRequestIdSurvivesObservationRefresh(t *testing.T) {
	p := ShopBuyParams{ObservationID: "first", ItemID: "(O)472", Quantity: 15, MaxTotalCost: 500, ReserveMoney: 100}
	a, _ := p.values("run-1")
	p.ObservationID = "second"
	b, _ := p.values("run-1")
	if a["request_id"] != b["request_id"] {
		t.Fatal("refresh could charge a duplicate purchase")
	}
	c, _ := p.values("run-2")
	if b["request_id"] == c["request_id"] {
		t.Fatal("independent goals incorrectly share receipts")
	}
	p.Quantity = 10
	d, _ := p.values("run-1")
	if b["request_id"] == d["request_id"] {
		t.Fatal("different order reused receipt")
	}
	if a["quantity"] != 15 || a["max_total_cost"] != 500 || a["reserve_money"] != 100 {
		t.Fatal("purchase limits not passed through")
	}
}
