package main

import "testing"

func TestLifeTimeConversion(t *testing.T) {
	tests := []struct {
		hhmm, minutes int
		valid         bool
	}{{600, 360, true}, {2350, 1430, true}, {2400, 1440, true}, {2550, 1550, true}, {2360, 0, false}, {-100, 0, false}}
	for _, test := range tests {
		got, ok := hhmmMinutes(test.hhmm)
		if ok != test.valid || got != test.minutes {
			t.Fatalf("hhmmMinutes(%d)=(%d,%v), want (%d,%v)", test.hhmm, got, ok, test.minutes, test.valid)
		}
		if ok && minutesHHMM(got) != test.hhmm {
			t.Fatalf("round trip failed for %d", test.hhmm)
		}
	}
}

func TestLifeInputValidationBeforeGameAccess(t *testing.T) {
	a := &StardewAgent{}
	if result, _ := a.consumeFood(ConsumeFoodParams{Slot: -1, ItemID: "(O)194", ReserveQuantity: 0}); result[:13] != "TASK_BLOCKED:" {
		t.Fatal("invalid slot was not blocked")
	}
	if result, _ := scheduleBedtime(BedtimeParams{TargetBedTime: 2360, BufferMinutes: 30}); result[:13] != "TASK_BLOCKED:" {
		t.Fatal("invalid bedtime was not blocked")
	}
	if result, _ := (&StardewAgent{}).manageDailyLife(DailyLifeParams{MinimumEnergyPercent: 99}); result[:13] != "TASK_BLOCKED:" {
		t.Fatal("invalid daily-life policy was not blocked")
	}
}

func TestDailyLifeDefaultsAndFoodPolicy(t *testing.T) {
	p, err := dailyLifeDefaults(DailyLifeParams{})
	if err != nil || p.MinimumEnergyPercent != 20 || p.ReturnHomeTime != 2200 || p.MaximumFoodSellPrice != 50 || p.ReserveQuantity != 1 || p.MaximumFoodItems != 2 {
		t.Fatalf("bad defaults: %+v %v", p, err)
	}
	energy10, energy25 := 10, 25
	foods := []observedFood{
		{Slot: 1, ItemID: "(O)A", Stack: 3, EnergyRecovery: &energy25, SellPrice: 80},
		{Slot: 2, ItemID: "(O)B", Stack: 2, EnergyRecovery: &energy10, SellPrice: 20},
		{Slot: 3, ItemID: "(O)C", Stack: 5, EnergyRecovery: &energy25, SellPrice: 10},
	}
	p.ProtectedItemIDs = []string{"(O)C"}
	chosen, ok := chooseDailyFood(foods, p)
	if !ok || chosen.ItemID != "(O)B" {
		t.Fatalf("food policy ignored value/reserve/protection: %+v", chosen)
	}
}
