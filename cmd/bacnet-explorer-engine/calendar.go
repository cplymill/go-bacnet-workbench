package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/propertyvalue"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/types"
)

type calendarRule struct {
	Kind    string `json:"kind"`
	Start   string `json:"start"`
	End     string `json:"end"`
	Weekday string `json:"weekday"`
	Month   string `json:"month"`
	Week    string `json:"week"`
}

func datePart(value byte) string {
	if value == 255 {
		return "*"
	}
	return strconv.Itoa(int(value))
}
func datePattern(date types.Date) string {
	year := "*"
	if date.Year != 255 {
		year = strconv.Itoa(1900 + int(date.Year))
	}
	month, day := datePart(date.Month), datePart(date.Day)
	if date.Month >= 1 && date.Month <= 12 {
		month = fmt.Sprintf("%02d", date.Month)
	}
	if date.Day >= 1 && date.Day <= 31 {
		day = fmt.Sprintf("%02d", date.Day)
	}
	return year + "-" + month + "-" + day
}
func ruleFromEntry(entry types.CalendarEntry) calendarRule {
	rule := calendarRule{Kind: "date", Weekday: "*", Month: "*", Week: "*"}
	switch entry.Choice {
	case types.CalendarEntryDate:
		rule.Start = datePattern(entry.Date)
		rule.Weekday = datePart(entry.Date.Weekday)
	case types.CalendarEntryDateRange:
		rule.Kind = "range"
		rule.Start = datePattern(entry.DateRange.StartDate)
		rule.End = datePattern(entry.DateRange.EndDate)
	case types.CalendarEntryWeekNDay:
		rule.Kind = "week"
		rule.Month = datePart(entry.WeekNDay.Month)
		rule.Week = datePart(entry.WeekNDay.WeekOfMonth)
		rule.Weekday = datePart(entry.WeekNDay.DayOfWeek)
	}
	return rule
}
func patternPart(text string, aliases map[string]byte) (byte, error) {
	text = strings.ToLower(strings.TrimSpace(text))
	if text == "" || text == "*" {
		return 255, nil
	}
	if value, ok := aliases[text]; ok {
		return value, nil
	}
	value, err := strconv.ParseUint(text, 10, 8)
	return byte(value), err
}
func parseDatePattern(text, weekday string) (types.Date, error) {
	parts := strings.Split(strings.TrimSpace(text), "-")
	if len(parts) != 3 {
		return types.Date{}, errors.New("use YYYY-MM-DD; * selects any year/month/day")
	}
	date := types.Date{Year: 255}
	if parts[0] != "*" {
		year, err := strconv.Atoi(parts[0])
		if err != nil || year < 1900 || year > 2154 {
			return date, errors.New("date year must be 1900..2154 or *")
		}
		date.Year = byte(year - 1900)
	}
	var err error
	date.Month, err = patternPart(parts[1], map[string]byte{"odd": 13, "even": 14})
	if err != nil {
		return date, err
	}
	date.Day, err = patternPart(parts[2], map[string]byte{"last": 32, "odd": 33, "even": 34})
	if err != nil {
		return date, err
	}
	date.Weekday, err = patternPart(weekday, map[string]byte{"mon": 1, "tue": 2, "wed": 3, "thu": 4, "fri": 5, "sat": 6, "sun": 7})
	if err != nil {
		return date, err
	}
	return date, date.Validate()
}
func rangeDate(text string) (types.Date, error) {
	if text == "" || text == "*" || text == "*-*-*" {
		return types.Date{Year: 255, Month: 255, Day: 255, Weekday: 255}, nil
	}
	date, err := time.Parse("2006-01-02", text)
	if err != nil {
		return types.Date{}, err
	}
	return types.DateFromTime(date)
}
func (rule calendarRule) entry() (types.CalendarEntry, error) {
	entry := types.CalendarEntry{}
	var err error
	switch rule.Kind {
	case "date":
		entry.Choice = types.CalendarEntryDate
		entry.Date, err = parseDatePattern(rule.Start, rule.Weekday)
	case "range":
		entry.Choice = types.CalendarEntryDateRange
		entry.DateRange.StartDate, err = rangeDate(rule.Start)
		if err == nil {
			entry.DateRange.EndDate, err = rangeDate(rule.End)
		}
	case "week":
		entry.Choice = types.CalendarEntryWeekNDay
		entry.WeekNDay.Month, err = patternPart(rule.Month, map[string]byte{"odd": 13, "even": 14})
		if err == nil {
			entry.WeekNDay.WeekOfMonth, err = patternPart(rule.Week, nil)
		}
		if err == nil {
			entry.WeekNDay.DayOfWeek, err = patternPart(rule.Weekday, map[string]byte{"mon": 1, "tue": 2, "wed": 3, "thu": 4, "fri": 5, "sat": 6, "sun": 7})
		}
	default:
		return entry, errors.New("calendar rule kind must be date, range or week")
	}
	if err != nil {
		return entry, err
	}
	return entry, entry.Validate()
}

type propertyEdit struct {
	Property types.PropertyIdentifier
	Value    []byte
	Expected string
}

// Reuses ordinary stack reads/writes, preflighting all loaded baselines before
// sending any changed property. Property writes remain independent operations.
func (s *session) savePropertyEdits(ctx context.Context, peer peerInfo, id types.ObjectIdentifier, edits []propertyEdit) (any, error) {
	changed := []propertyEdit{}
	for _, edit := range edits {
		if len(edit.Expected) > 512<<10 {
			return nil, errors.New("loaded property exceeds the editor limit")
		}
		expected, err := hex.DecodeString(edit.Expected)
		if err != nil {
			return nil, err
		}
		current, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: edit.Property}, encoding.Limits{})
		if err != nil {
			return nil, err
		}
		if !bytes.Equal(current.Value, expected) {
			return nil, fmt.Errorf("%s changed on the device; refresh before saving", edit.Property)
		}
		if !bytes.Equal(current.Value, edit.Value) {
			changed = append(changed, edit)
		}
	}
	result := map[string]any{}
	for _, edit := range changed {
		written, err := s.write(ctx, peer, operationArgs{Object: objectText(id), Property: fmt.Sprint(uint32(edit.Property)), ValueType: "raw", Value: hex.EncodeToString(edit.Value), Confirmed: true})
		if err != nil {
			return nil, fmt.Errorf("%s save failed after %d acknowledged property writes: %w; refresh before retrying", edit.Property, len(result), err)
		}
		result[edit.Property.String()] = written
	}
	result["unchanged"] = len(changed) == 0
	return result, nil
}

func (s *session) readCalendar(ctx context.Context, peer peerInfo, text string) (any, error) {
	id, err := types.ParseObjectIdentifier(text)
	if err != nil {
		return nil, err
	}
	if id.Type() != types.ObjectTypeCalendar {
		return nil, errors.New("select a Calendar object")
	}
	ack, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyDateList}, encoding.Limits{})
	if err != nil {
		return nil, err
	}
	rules := []calendarRule{}
	remaining := ack.Value
	for len(remaining) > 0 {
		if len(rules) >= 1024 {
			return nil, errors.New("calendar exceeds 1024 entries")
		}
		entry, used, err := encoding.DecodeCalendarEntry(remaining, encoding.Limits{})
		if err != nil {
			return nil, err
		}
		rules = append(rules, ruleFromEntry(entry))
		remaining = remaining[used:]
	}
	result := map[string]any{"rules": rules, "raw": hex.EncodeToString(ack.Value)}
	if present, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyPresentValue}, encoding.Limits{}); err == nil {
		result["presentValue"] = propertyvalue.FormatPrimitive(present.Value)
	}
	return result, nil
}
func (s *session) writeCalendar(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed || args.ExpectedListRaw == nil || args.Rules == nil || len(args.Rules) > 1024 {
		return nil, errors.New("confirm a loaded Calendar with at most 1024 entries")
	}
	id, err := types.ParseObjectIdentifier(args.Object)
	if err != nil {
		return nil, err
	}
	if id.Type() != types.ObjectTypeCalendar {
		return nil, errors.New("select a Calendar object")
	}
	value := []byte{}
	for _, rule := range args.Rules {
		entry, err := rule.entry()
		if err != nil {
			return nil, err
		}
		value, err = encoding.AppendCalendarEntry(value, entry)
		if err != nil {
			return nil, err
		}
	}
	return s.savePropertyEdits(ctx, peer, id, []propertyEdit{{Property: types.PropertyDateList, Value: value, Expected: *args.ExpectedListRaw}})
}
