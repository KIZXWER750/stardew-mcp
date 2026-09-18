package main

import "testing"

func TestCropSaleLimits(t *testing.T) {
	good := CropSaleParams{QuoteID: "observed", KeepQuantity: 3, MinimumTotalPrice: 10}
	v, e := good.values()
	if e != nil || v["keep_quantity"] != 3 || v["minimum_total_price"] != 10 {
		t.Fatal("lost sale limits", v, e)
	}
	for _, bad := range []CropSaleParams{{QuoteID: ""}, {QuoteID: "q", KeepQuantity: -1, MinimumTotalPrice: 1}, {QuoteID: "q", MinimumTotalPrice: 0}} {
		if _, e := bad.values(); e == nil {
			t.Fatal("accepted invalid sale", bad)
		}
	}
}
func TestStorageTakeLimits(t *testing.T) {
	v, e := (StorageTakeParams{QuoteID: "q", KeepInChest: 5}).values()
	if e != nil || v["keep_in_chest"] != 5 {
		t.Fatal(v, e)
	}
	for _, bad := range []StorageTakeParams{{QuoteID: ""}, {QuoteID: "q", KeepInChest: -1}} {
		if _, e := bad.values(); e == nil {
			t.Fatal("accepted invalid withdrawal")
		}
	}
}
