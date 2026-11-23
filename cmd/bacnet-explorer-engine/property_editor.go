package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/propertyvalue"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/types"
)

func propertyEditorPrimitive(wire []byte) (string, string) {
	_, consumed, err := encoding.DecodeApplicationPrimitive(wire, encoding.Limits{})
	if err != nil || consumed != len(wire) {
		return "raw", hex.EncodeToString(wire)
	}
	switch valueType(wire) {
	case "date":
		value, _, err := encoding.DecodeDate(wire, encoding.Limits{})
		if err == nil {
			dateText := datePattern(value)
			parts := strings.Split(dateText, "-")
			if value.Month == 13 {
				parts[1] = "odd"
			} else if value.Month == 14 {
				parts[1] = "even"
			}
			if value.Day == 32 {
				parts[2] = "last"
			} else if value.Day == 33 {
				parts[2] = "odd"
			} else if value.Day == 34 {
				parts[2] = "even"
			}
			return "date", strings.Join(parts, "-") + "|" + strconv.Itoa(int(value.Weekday))
		}
	case "time":
		value, _, err := encoding.DecodeTime(wire, encoding.Limits{})
		if err == nil {
			part := func(value byte, width int) string {
				if value == 255 {
					return "*"
				}
				return fmt.Sprintf("%0*d", width, value)
			}
			return "time", part(value.Hour, 2) + ":" + part(value.Minute, 2) + ":" + part(value.Second, 2) + "." + part(value.Hundredths, 2)
		}
	case "bit-string":
		value, _, err := encoding.DecodeBitString(wire, encoding.Limits{})
		if err == nil {
			var text strings.Builder
			for bit := uint32(0); bit < value.Len(); bit++ {
				enabled, _ := value.Bit(bit)
				if enabled {
					text.WriteByte('1')
				} else {
					text.WriteByte('0')
				}
			}
			return "bit-string", text.String()
		}
	}
	return editablePrimitive(wire)
}

func propertyEditorArray(wire []byte, force bool) ([]string, []string) {
	kinds, values := []string{}, []string{}
	for len(wire) > 0 && len(values) < 4096 {
		_, count, err := encoding.DecodeApplicationPrimitive(wire, encoding.Limits{})
		if err != nil || count < 1 {
			return nil, nil
		}
		kind, value := propertyEditorPrimitive(wire[:count])
		if kind == "raw" {
			return nil, nil
		}
		kinds, values = append(kinds, kind), append(values, value)
		wire = wire[count:]
	}
	if len(wire) > 0 || len(values) < 2 && !force {
		return nil, nil
	}
	return kinds, values
}

func encodePropertyEditorValue(kind, value string) ([]byte, error) {
	switch kind {
	case "date":
		parts := strings.Split(value, "|")
		weekday := "*"
		if len(parts) == 2 {
			weekday = parts[1]
		}
		date, err := parseDatePattern(parts[0], weekday)
		if err != nil {
			return nil, err
		}
		return encoding.AppendDate(nil, date)
	case "time":
		parts := strings.Split(strings.ReplaceAll(strings.TrimSpace(value), ".", ":"), ":")
		if len(parts) < 2 || len(parts) > 4 {
			return nil, errors.New("use HH:mm[:ss[.ff]], with * for unspecified fields")
		}
		bytes := [4]byte{}
		for index, part := range parts {
			if part == "*" {
				bytes[index] = 255
				continue
			}
			if index == 3 && len(part) == 1 {
				part += "0"
			}
			number, err := strconv.ParseUint(part, 10, 8)
			limits := [4]uint64{23, 59, 59, 99}
			if err != nil || number > limits[index] || index == 3 && len(part) > 2 {
				return nil, errors.New("time components are HH 0–23, mm/ss 0–59 and hundredths 0–99, or *")
			}
			bytes[index] = byte(number)
		}
		return encoding.AppendTime(nil, types.TimeOfDay{Hour: bytes[0], Minute: bytes[1], Second: bytes[2], Hundredths: bytes[3]})
	case "bit-string":
		if len(value) > 65536 {
			return nil, errors.New("too many flag bits")
		}
		bits := make([]bool, len(value))
		for index, bit := range value {
			if bit != '0' && bit != '1' {
				return nil, errors.New("flags must contain 0 or 1")
			}
			bits[index] = bit == '1'
		}
		return encoding.AppendBitString(nil, types.BitStringFromBools(bits...))
	}
	return propertyvalue.Encode(kind, value)
}

func (s *session) encodeEditorWrite(ctx context.Context, peer peerInfo, args operationArgs) ([]byte, error) {
	if args.ValueType != "primitive-array-json" {
		if args.Property == "units" && args.ValueType == "enumerated" {
			unit, err := types.ParseEngineeringUnits(strings.ReplaceAll(strings.ToLower(strings.TrimSpace(args.Value)), " ", "-"))
			if err != nil {
				return nil, err
			}
			return encoding.AppendEnumerated(nil, uint64(unit)), nil
		}
		return encodePropertyEditorValue(args.ValueType, args.Value)
	}
	var values []string
	if err := json.Unmarshal([]byte(args.Value), &values); err != nil || len(values) > 4096 {
		return nil, errors.New("enter up to 4096 array values")
	}
	selection, err := parseSelection(selection{Object: args.Object, Property: args.Property, Index: args.Index})
	if err != nil {
		return nil, err
	}
	current, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: selection.ObjectIdentifier, PropertyIdentifier: selection.PropertyIdentifier, ArrayIndex: service.ArrayIndex{}}, encoding.Limits{})
	if err != nil {
		return nil, err
	}
	kinds, _ := propertyEditorArray(current.Value, true)
	if len(kinds) == 0 {
		return nil, errors.New("array element types could not be read")
	}
	if len(values) != len(kinds) {
		return nil, errors.New("keep the existing number of array items; edit array size separately if the device supports it")
	}
	var wire []byte
	for index, value := range values {
		item, err := encodePropertyEditorValue(kinds[index], value)
		if err != nil {
			return nil, fmt.Errorf("item %d: %w", index+1, err)
		}
		wire = append(wire, item...)
		if len(wire) > 65536 {
			return nil, errors.New("array exceeds the write limit")
		}
	}
	return wire, nil
}

func engineeringUnitChoices() []map[string]string {
	result := []map[string]string{}
	for number := uint32(0); number < 65536; number++ {
		unit := types.EngineeringUnits(number)
		if unit.AssignedAtRevision(30) {
			result = append(result, map[string]string{"value": strconv.FormatUint(uint64(number), 10), "label": strings.ReplaceAll(unit.String(), "-", " ")})
		}
	}
	return result
}
