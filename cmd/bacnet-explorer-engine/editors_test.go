package main

import (
	"bytes"
	"testing"

	"github.com/cplymill/go-bacnet/encoding"
)

func TestCalendarEditorPatternsPreserveWireValues(t *testing.T) {
	rules := []calendarRule{
		{Kind: "date", Start: "*-12-25", Weekday: "*"},
		{Kind: "date", Start: "*-even-last", Weekday: "Mon"},
		{Kind: "range", Start: "2026-11-02", End: "2026-11-06"},
		{Kind: "range", Start: "*", End: "*"},
		{Kind: "week", Month: "odd", Week: "6", Weekday: "Fri"},
		{Kind: "week", Month: "*", Week: "9", Weekday: "*"},
	}
	for _, rule := range rules {
		entry, err := rule.entry()
		if err != nil {
			t.Fatal(err)
		}
		wire, err := encoding.AppendCalendarEntry(nil, entry)
		if err != nil {
			t.Fatal(err)
		}
		decoded, used, err := encoding.DecodeCalendarEntry(wire, encoding.Limits{})
		if err != nil || used != len(wire) {
			t.Fatalf("decode: %v", err)
		}
		restored, err := ruleFromEntry(decoded).entry()
		if err != nil {
			t.Fatal(err)
		}
		encoded, err := encoding.AppendCalendarEntry(nil, restored)
		if err != nil || !bytes.Equal(wire, encoded) {
			t.Fatalf("calendar editor changed a rule: %+v", rule)
		}
	}
	for _, rule := range []calendarRule{{Kind: "date", Start: "1800-01-01"}, {Kind: "range", Start: "2026-11-06", End: "2026-11-02"}, {Kind: "week", Month: "15", Week: "1", Weekday: "Mon"}, {Kind: "week", Month: "*", Week: "10", Weekday: "*"}} {
		if _, err := rule.entry(); err == nil {
			t.Fatalf("invalid rule accepted: %+v", rule)
		}
	}
}
