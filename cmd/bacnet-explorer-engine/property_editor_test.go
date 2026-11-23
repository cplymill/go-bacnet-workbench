package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"net/netip"
	"reflect"
	"sync"
	"testing"
	"time"

	"github.com/cplymill/go-bacnet/datalink/bip"
	"github.com/cplymill/go-bacnet/device"
	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/transaction"
	"github.com/cplymill/go-bacnet/types"
)

// 20.2: natural input reuses the shared primitive encoders without changing tags.
func TestPropertyEditorNaturalPrimitiveRoundTrips(t *testing.T) {
	for _, test := range []struct{ kind, text string }{
		{"date", "2026-11-03|2"}, {"date", "*-odd-last|255"},
		{"time", "08:30:15.25"}, {"time", "*:30:*.00"},
		{"bit-string", "10101"}, {"character-string", "Device East"},
		{"object-identifier", "8:1001"}, {"enumerated", "64"},
	} {
		wire, err := encodePropertyEditorValue(test.kind, test.text)
		if err != nil {
			t.Fatalf("%s: %v", test.kind, err)
		}
		kind, text := propertyEditorPrimitive(wire)
		again, err := encodePropertyEditorValue(kind, text)
		if err != nil || !bytes.Equal(wire, again) {
			t.Fatalf("%s round trip: %s %q %v", test.kind, kind, text, err)
		}
	}
	for _, test := range []struct{ kind, text string }{{"time", "25:00"}, {"date", "2026-16-20"}, {"bit-string", "10x"}} {
		if _, err := encodePropertyEditorValue(test.kind, test.text); err == nil {
			t.Fatalf("accepted invalid %s %q", test.kind, test.text)
		}
	}
}

// 12.11.1/.2 and 15.9: the remote fixture permits identity writes. The test
// exercises production UDP/services and proves new-addressed readback/rebinding.
func TestPropertyEditorDeviceIdentityAndLabelsLoopback(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	var mu sync.Mutex
	id, _ := types.NewObjectIdentifier(types.ObjectTypeDevice, 901)
	valueID, _ := types.NewObjectIdentifier(types.ObjectTypeMultiStateValue, 1)
	initial := id
	name, _ := types.NewUTF8String("Before")
	labels := []string{"Off", "On"}
	writes := 0
	properties := device.PropertyExecutorFuncs{
		ReadFunc: func(_ context.Context, request service.ReadPropertyRequest) ([]byte, error) {
			mu.Lock()
			defer mu.Unlock()
			if request.ObjectIdentifier != id && request.ObjectIdentifier != valueID {
				return nil, service.ProtocolError{Class: service.ErrorClassObject, Code: service.ErrorCodeUnknownObject}
			}
			switch request.PropertyIdentifier {
			case types.PropertyObjectIdentifier:
				return encoding.AppendObjectIdentifier(nil, id), nil
			case types.PropertyObjectName:
				return encoding.AppendCharacterString(nil, name)
			case types.PropertyStateText:
				var wire []byte
				for _, text := range labels {
					stringValue, _ := types.NewUTF8String(text)
					wire, _ = encoding.AppendCharacterString(wire, stringValue)
				}
				return wire, nil
			}
			return nil, service.ProtocolError{Class: service.ErrorClassProperty, Code: service.ErrorCodeUnknownProperty}
		},
		WriteFunc: func(_ context.Context, request service.WritePropertyRequest) error {
			mu.Lock()
			defer mu.Unlock()
			if request.ObjectIdentifier != id && request.ObjectIdentifier != valueID {
				return service.ProtocolError{Class: service.ErrorClassObject, Code: service.ErrorCodeUnknownObject}
			}
			writes++
			var err error
			switch request.PropertyIdentifier {
			case types.PropertyObjectIdentifier:
				id, _, err = encoding.DecodeObjectIdentifier(request.Value, encoding.Limits{})
			case types.PropertyObjectName:
				name, _, err = encoding.DecodeCharacterString(request.Value, encoding.Limits{})
			case types.PropertyStateText:
				_, labels = propertyEditorArray(request.Value, true)
			default:
				return service.ProtocolError{Class: service.ErrorClassProperty, Code: service.ErrorCodeWriteAccessDenied}
			}
			return err
		},
	}
	handler := device.ApplicationServerHandler{IAm: service.IAm{DeviceIdentifier: initial, MaxAPDU: 1476, Segmentation: service.SegmentationBoth, VendorID: 999}, Properties: properties}
	server, err := device.ListenNormalIPNode(device.IPNodeConfig{Port: bip.PortConfig{Local: netip.MustParseAddrPort("127.0.0.1:0"), Broadcast: netip.MustParseAddrPort("127.0.0.1:47808")}, Application: transaction.ApplicationConfig{Server: handler}})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- server.Run(ctx) }()
	t.Cleanup(func() { cancel(); _ = server.Close(); <-done })
	if err := server.Port().WaitReady(ctx); err != nil {
		t.Fatal(err)
	}
	for !server.Running() {
		select {
		case <-ctx.Done():
			t.Fatal(ctx.Err())
		case <-time.After(time.Millisecond):
		}
	}
	b := &backend{}
	t.Cleanup(func() { _ = b.disconnect() })
	if _, err := b.connect(ctx, connectionConfig{Transport: "ip", Bind: "127.0.0.1:0", Broadcast: server.LocalAddr().String(), TimeoutMS: 500}); err != nil {
		t.Fatal(err)
	}
	discovered, err := b.session.discover(ctx, operationArgs{Target: server.LocalAddr().String(), Seconds: 1})
	if err != nil || len(discovered.([]peerInfo)) != 1 {
		t.Fatalf("discovery: %v %v", discovered, err)
	}
	peer := discovered.([]peerInfo)[0]
	originalKey := peer.Key
	result, err := b.session.write(ctx, peer, operationArgs{Object: peer.Device, Property: "object-name", ValueType: "character-string", Value: "After", Confirmed: true})
	if err != nil {
		t.Fatal(err)
	}
	rows := result.(map[string]any)["readback"].(map[string]any)["rows"].([]propertyRow)
	if rows[0].EditorValue != "After" || b.session.devices()[0].Name != "After" {
		t.Fatalf("name not verified: %+v", rows)
	}
	peer, _ = b.session.lookup(peer.Key)
	result, err = b.session.write(ctx, peer, operationArgs{Object: peer.Device, Property: "object-identifier", ValueType: "object-identifier", Value: "8:1001", Confirmed: true})
	if err != nil {
		t.Fatal(err)
	}
	rows = result.(map[string]any)["readback"].(map[string]any)["rows"].([]propertyRow)
	if rows[0].Object != "8:1001" || rows[0].EditorValue != "8:1001" || rows[0].Error != "" {
		t.Fatalf("readback targeted old identity: %+v", rows)
	}
	peers := b.session.devices()
	if len(peers) != 1 || peers[0].Device != "8:1001" || peers[0].Key == originalKey {
		t.Fatalf("rebind: %+v", peers)
	}
	peer, err = b.session.lookup(peers[0].Key)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = b.session.lookup(originalKey); err == nil {
		t.Fatal("old identity retained")
	}
	result, err = b.session.write(ctx, peer, operationArgs{Object: "19:1", Property: "state-text", ValueType: "primitive-array-json", Value: `["Stopped","Running"]`, Confirmed: true})
	if err != nil {
		t.Fatal(err)
	}
	rows = result.(map[string]any)["readback"].(map[string]any)["rows"].([]propertyRow)
	if !reflect.DeepEqual(rows[0].ArrayValues, []string{"Stopped", "Running"}) {
		t.Fatalf("labels: %+v", rows)
	}
	mu.Lock()
	before := writes
	mu.Unlock()
	if _, err = b.session.write(ctx, peer, operationArgs{Object: peer.Device, Property: "object-identifier", ValueType: "object-identifier", Value: "3:1002", Confirmed: true}); err == nil {
		t.Fatal("object type change allowed")
	}
	mu.Lock()
	defer mu.Unlock()
	if writes != before {
		t.Fatal("invalid identity was sent")
	}
	if _, err := hex.DecodeString(rows[0].Raw); err != nil {
		t.Fatal(err)
	}
}
