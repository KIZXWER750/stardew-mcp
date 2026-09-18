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
}
