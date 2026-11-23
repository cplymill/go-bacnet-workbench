package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"strings"

	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/propertyvalue"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/types"
)

type scheduleEntry struct {
	Time  string `json:"time"`
	Type  string `json:"type"`
	Value string `json:"value"`
}

type scheduleException struct {
	Rule              calendarRule    `json:"rule"`
	CalendarReference string          `json:"calendarReference"`
	Priority          uint8           `json:"priority"`
	Times             []scheduleEntry `json:"times"`
}

func scheduleIdentifier(text string) (types.ObjectIdentifier, error) {
	id, err := types.ParseObjectIdentifier(text)
	if err == nil && id.Type() != types.ObjectTypeSchedule {
		err = errors.New("select a Schedule object")
	}
	return id, err
}

func editablePrimitive(wire []byte) (string, string) {
	kind := valueType(wire)
	value := propertyvalue.FormatPrimitive(wire)
	switch kind {
	case "null":
		return kind, ""
	case "enumerated":
		return kind, strings.TrimPrefix(value, "enumerated:")
	case "character-string":
		decoded, _, err := encoding.DecodeCharacterString(wire, encoding.Limits{})
		if err == nil {
			if text, err := decoded.Text(); err == nil {
				return kind, text
			}
		}
	case "object-identifier":
		decoded, _, err := encoding.DecodeObjectIdentifier(wire, encoding.Limits{})
		if err == nil {
			return kind, objectText(decoded)
		}
	case "octet-string":
		return kind, strings.TrimPrefix(value, "octets:")
	case "boolean", "real", "double", "unsigned", "signed":
		return kind, value
	}
	return "raw", hex.EncodeToString(wire)
}

func (s *session) readSchedule(ctx context.Context, peer peerInfo, text string) (any, error) {
	id, err := scheduleIdentifier(text)
	if err != nil {
		return nil, err
	}
	ack, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyWeeklySchedule}, encoding.Limits{})
	if err != nil {
		return nil, err
	}
	days := make([][]scheduleEntry, 0, 7)
	remaining := ack.Value
	for day := 0; day < 7; day++ {
		daily, used, err := encoding.DecodeDailySchedule(remaining, encoding.Limits{}, 256)
		if err != nil {
			return nil, fmt.Errorf("weekly schedule day %d: %w", day+1, err)
		}
		entries := make([]scheduleEntry, 0, len(daily.TimeValues))
		for _, item := range daily.TimeValues {
			kind, value := editablePrimitive(item.Value)
			entries = append(entries, scheduleEntry{Time: fmt.Sprintf("%02d:%02d:%02d.%02d", item.Time.Hour, item.Time.Minute, item.Time.Second, item.Time.Hundredths), Type: kind, Value: value})
		}
		days = append(days, entries)
		remaining = remaining[used:]
	}
	if len(remaining) != 0 {
		return nil, errors.New("weekly schedule contains trailing values")
	}
	result := map[string]any{"days": days, "raw": hex.EncodeToString(ack.Value)}
	fallback, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyScheduleDefault}, encoding.Limits{})
	if err == nil {
		result["defaultType"], result["defaultValue"] = editablePrimitive(fallback.Value)
		result["defaultDisplay"] = propertyvalue.FormatPrimitive(fallback.Value)
		result["defaultRaw"] = hex.EncodeToString(fallback.Value)
	} else {
		result["defaultError"] = err.Error()
	}
	exceptionACK, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyExceptionSchedule}, encoding.Limits{})
	if err == nil {
		exceptions := []scheduleException{}
		remaining := exceptionACK.Value
		for len(remaining) > 0 {
			if len(exceptions) >= 256 {
				err = errors.New("exception schedule exceeds 256 entries")
				break
			}
			event, used, decodeErr := encoding.DecodeSpecialEvent(remaining, encoding.Limits{}, 256)
			if decodeErr != nil {
				err = decodeErr
				break
			}
			row := scheduleException{Priority: event.EventPriority, Times: []scheduleEntry{}}
			if event.PeriodChoice == types.SpecialEventCalendarReference {
				row.CalendarReference = objectText(event.CalendarReference)
			} else {
				row.Rule = ruleFromEntry(event.CalendarEntry)
			}
			for _, value := range event.TimeValues {
				kind, text := editablePrimitive(value.Value)
				row.Times = append(row.Times, scheduleEntry{Time: fmt.Sprintf("%02d:%02d:%02d.%02d", value.Time.Hour, value.Time.Minute, value.Time.Second, value.Time.Hundredths), Type: kind, Value: text})
			}
			exceptions = append(exceptions, row)
			remaining = remaining[used:]
		}
		if err == nil {
			result["exceptions"] = exceptions
			result["exceptionsRaw"] = hex.EncodeToString(exceptionACK.Value)
		}
	}
	if err != nil {
		result["exceptionsError"] = err.Error()
	}
	periodACK, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyEffectivePeriod}, encoding.Limits{})
	if err == nil {
		period, used, decodeErr := encoding.DecodeDateRange(periodACK.Value, encoding.Limits{})
		err = decodeErr
		if err == nil && used != len(periodACK.Value) {
			err = errors.New("effective period has trailing values")
		}
		if err == nil {
			result["periodStart"] = datePattern(period.StartDate)
			result["periodEnd"] = datePattern(period.EndDate)
			result["periodRaw"] = hex.EncodeToString(periodACK.Value)
		}
	}
	if err != nil {
		result["periodError"] = err.Error()
	}
	return result, nil
}

func encodeExceptions(rows []scheduleException) ([]byte, error) {
	if len(rows) > 256 {
		return nil, errors.New("exception schedule exceeds 256 entries")
	}
	value := []byte{}
	for _, row := range rows {
		event := types.SpecialEvent{EventPriority: row.Priority}
		if row.CalendarReference != "" {
			id, err := types.ParseObjectIdentifier(row.CalendarReference)
			if err != nil {
				return nil, err
			}
			event.PeriodChoice, event.CalendarReference = types.SpecialEventCalendarReference, id
		} else {
			rule, err := row.Rule.entry()
			if err != nil {
				return nil, err
			}
			event.PeriodChoice, event.CalendarEntry = types.SpecialEventCalendarEntry, rule
		}
		// Reuse the existing value adapter's time/value admission, then the
		// protocol owner's SpecialEvent codec for the actual wire value.
		days := [7][]scheduleEntry{}
		days[0] = row.Times
		text, err := json.Marshal(days)
		if err != nil {
			return nil, err
		}
		wire, err := propertyvalue.Encode("weekly-schedule-json", string(text))
		if err != nil {
			return nil, err
		}
		daily, _, err := encoding.DecodeDailySchedule(wire, encoding.Limits{}, 256)
		if err != nil {
			return nil, err
		}
		event.TimeValues = daily.TimeValues
		value, err = encoding.AppendSpecialEvent(value, event)
		if err != nil {
			return nil, err
		}
	}
	return value, nil
}

func (s *session) writeSchedule(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed || args.ExpectedRaw == "" {
		return nil, errors.New("confirm a loaded weekly schedule before saving")
	}
	id, err := scheduleIdentifier(args.Object)
	if err != nil {
		return nil, err
	}
	weekly, err := propertyvalue.Encode("weekly-schedule-json", args.Value)
	if err != nil {
		return nil, err
	}
	edits := []propertyEdit{{Property: types.PropertyWeeklySchedule, Value: weekly, Expected: args.ExpectedRaw}}
	if args.DefaultType != "" {
		if args.ExpectedDefaultRaw == "" {
			return nil, errors.New("refresh the schedule default before saving it")
		}
		value, err := propertyvalue.Encode(args.DefaultType, args.DefaultValue)
		if err != nil {
			return nil, err
		}
		if _, err = encoding.ValidateApplicationPrimitive(value, encoding.Limits{}); err != nil {
			return nil, err
		}
		edits = append(edits, propertyEdit{Property: types.PropertyScheduleDefault, Value: value, Expected: args.ExpectedDefaultRaw})
	}
	if args.Exceptions != nil {
		if args.ExpectedExceptionsRaw == nil {
			return nil, errors.New("refresh exceptions before saving")
		}
		value, err := encodeExceptions(args.Exceptions)
		if err != nil {
			return nil, err
		}
		edits = append(edits, propertyEdit{Property: types.PropertyExceptionSchedule, Value: value, Expected: *args.ExpectedExceptionsRaw})
	}
	if args.ExpectedPeriodRaw != nil {
		start, err := rangeDate(args.PeriodStart)
		if err != nil {
			return nil, err
		}
		end, err := rangeDate(args.PeriodEnd)
		if err != nil {
			return nil, err
		}
		value, err := encoding.AppendDateRange(nil, types.DateRange{StartDate: start, EndDate: end})
		if err != nil {
			return nil, err
		}
		edits = append(edits, propertyEdit{Property: types.PropertyEffectivePeriod, Value: value, Expected: *args.ExpectedPeriodRaw})
	}
	return s.savePropertyEdits(ctx, peer, id, edits)
}
