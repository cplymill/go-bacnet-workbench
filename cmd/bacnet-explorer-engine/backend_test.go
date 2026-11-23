package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net/netip"
	"strings"
	"testing"
	"time"

	"github.com/cplymill/go-bacnet/apdu"
	"github.com/cplymill/go-bacnet/datalink"
	"github.com/cplymill/go-bacnet/datalink/bip"
	"github.com/cplymill/go-bacnet/datalink/mstp"
	"github.com/cplymill/go-bacnet/datalink/mstp/virtual"
	"github.com/cplymill/go-bacnet/device"
	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/network"
	"github.com/cplymill/go-bacnet/npdu"
	"github.com/cplymill/go-bacnet/object"
	"github.com/cplymill/go-bacnet/propertyvalue"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/transaction"
	"github.com/cplymill/go-bacnet/types"
)

func TestExplorerPriorityTableKeepsSlotBoundaries(t *testing.T) {
	wire := []byte{}
	for slot := 0; slot < 16; slot++ {
		value := encoding.AppendNull(nil)
		if slot == 0 {
			value = encoding.AppendReal(nil, 12.5)
		}
		if slot == 15 {
			var err error
			value, err = propertyvalue.Encode("character-string", "value, with comma")
			if err != nil {
				t.Fatal(err)
			}
		}
		wire = append(wire, value...)
	}
	id, _ := types.NewObjectIdentifier(types.ObjectTypeAnalogValue, 1)
	row := makeRow("fixture", id, service.PropertyResult{PropertyIdentifier: types.PropertyPriorityArray, Value: wire})
	if len(row.Elements) != 16 || row.Elements[0] != "12.5" || row.Elements[1] != "null" || row.Elements[15] != `"value, with comma"` {
		t.Fatalf("priority table: %+v", row.Elements)
	}
	row = makeRow("fixture", id, service.PropertyResult{PropertyIdentifier: types.PropertyPriorityArray, Value: wire[:len(wire)-1]})
	if len(row.Elements) != 0 {
		t.Fatal("malformed priority array displayed as complete")
	}
}

// Exercises the desktop adapter against the production UDP, service, registry
// and priority owners: Clauses 16.10, 15.5, 15.7, 15.9 and 19.2 are reused.
func TestExplorerLoopbackWorkflow(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	deviceID, _ := types.NewObjectIdentifier(types.ObjectTypeDevice, 991)
	analogID, _ := types.NewObjectIdentifier(types.ObjectTypeAnalogValue, 1)
	objectIDs := []types.ObjectIdentifier{deviceID}
	for instance := 1; instance < 500; instance++ {
		id, _ := types.NewObjectIdentifier(types.ObjectTypeAnalogValue, types.ObjectInstance(instance))
		objectIDs = append(objectIDs, id)
	}
	name, _ := types.NewUTF8String("Explorer test")
	registry := device.NewObjectRegistry()
	handler := device.ApplicationServerHandler{IAm: service.IAm{DeviceIdentifier: deviceID, MaxAPDU: 1476, Segmentation: service.SegmentationBoth, VendorID: 999}, Properties: registry}
	services, _ := types.ServicesSupportedBitString(handler.ExecutedServices()...)
	localDevice, err := object.NewDevice(object.DeviceConfig{Identifier: deviceID, Name: name, VendorName: name, VendorIdentifier: 999, ModelName: name, FirmwareRevision: name, ApplicationSoftwareVersion: name,
		ServicesSupported: services, ObjectList: objectIDs, MaxAPDULengthAccepted: 1476, SegmentationSupported: service.SegmentationBoth, MaxSegmentsAccepted: 64, APDUSegmentTimeout: 5000, APDUTimeout: 6000, NumberOfAPDURetries: 3, DatabaseRevision: 1})
	if err != nil {
		t.Fatal(err)
	}
	analog, err := object.NewAnalogValue(object.AnalogValueConfig{Identifier: analogID, Name: name, RelinquishDefault: 12, Units: types.EngineeringUnitsDegreesCelsius})
	if err != nil {
		t.Fatal(err)
	}
	if err = registry.Register(deviceID, localDevice); err != nil {
		t.Fatal(err)
	}
	if err = registry.Register(analogID, analog); err != nil {
		t.Fatal(err)
	}
	// A peer that cannot return one large property must not block inspection
	// of its other properties or indexed Object_List acquisition.
	limitedHandler := transaction.RequestHandlerFunc(func(ctx context.Context, incoming transaction.Request) ([]byte, error) {
		if request, options, err := service.ParseReadPropertyMultiple(incoming.APDU(), service.MultipleLimits{}); err == nil {
			for _, spec := range request.Specifications {
				for _, property := range spec.Properties {
					if property.PropertyIdentifier == types.PropertyAll || property.PropertyIdentifier == types.PropertyUnits || property.PropertyIdentifier == types.PropertyObjectList && !property.ArrayIndex.Present {
						return apdu.AppendAbortResponse(nil, apdu.AbortResponse{InvokeID: options.InvokeID, Server: true, Reason: apdu.AbortSegmentationNotSupported}), nil
					}
				}
			}
		}
		if request, options, err := service.ParseReadProperty(incoming.APDU(), encoding.Limits{}); err == nil && (request.PropertyIdentifier == types.PropertyUnits || request.PropertyIdentifier == types.PropertyObjectList && !request.ArrayIndex.Present) {
			return apdu.AppendAbortResponse(nil, apdu.AbortResponse{InvokeID: options.InvokeID, Server: true, Reason: apdu.AbortSegmentationNotSupported}), nil
		}
		return handler.HandleBACnetRequest(ctx, incoming)
	})
	server, err := device.ListenNormalIPNode(device.IPNodeConfig{Port: bip.PortConfig{Local: netip.MustParseAddrPort("127.0.0.1:0"), Broadcast: netip.MustParseAddrPort("127.0.0.1:47808")}, Application: transaction.ApplicationConfig{Server: limitedHandler}})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- server.Run(ctx) }()
	t.Cleanup(func() { cancel(); _ = server.Close(); <-done })
	if err = server.Port().WaitReady(ctx); err != nil {
		t.Fatal(err)
	}
	for !server.Running() {
		select {
		case <-ctx.Done():
			t.Fatal(ctx.Err())
		case runErr := <-done:
			done <- runErr
			t.Fatal(runErr)
		case <-time.After(time.Millisecond):
		}
	}
	b := &backend{}
	t.Cleanup(func() { _ = b.disconnect() })
	if _, err = b.connect(ctx, connectionConfig{Transport: "ip", Bind: "127.0.0.1:0", Broadcast: server.LocalAddr().String(), TimeoutMS: 500, Retries: 0}); err != nil {
		t.Fatal(err)
	}
	progressCount := 0
	progressContext := context.WithValue(ctx, progressKey{}, func(result any) {
		progressCount++
		if _, ok := result.([]peerInfo); !ok {
			t.Errorf("unexpected discovery progress: %T", result)
		}
	})
	devices, err := b.session.discover(progressContext, operationArgs{Target: server.LocalAddr().String(), Seconds: 1})
	if err != nil {
		t.Fatal(err)
	}
	peers := devices.([]peerInfo)
	select {
	case runErr := <-done:
		done <- runErr
		t.Fatalf("server stopped: %v", runErr)
	default:
	}
	if len(peers) != 1 || peers[0].Instance != 991 {
		t.Fatalf("discovery: %+v; target=%v; serverport=%+v; clientport=%+v; servernet=%+v", peers, server.LocalAddr(), server.Port().Stats(), b.session.node.Port().(*bip.Port).Stats(), server.Network().Stats())
	}
	if progressCount == 0 {
		t.Fatal("discovery did not publish interim results")
	}
	if peers[0].Name != "Explorer test" {
		t.Fatalf("device name was not resolved during discovery: %q", peers[0].Name)
	}
	peer := peers[0]
	deviceProperties, err := b.session.properties(ctx, peer, "8:991")
	if err != nil {
		t.Fatalf("Device inspection blocked by an oversized array: %v", err)
	}
	listedCount := false
	for _, row := range deviceProperties.(map[string]any)["rows"].([]propertyRow) {
		if row.Property == "object-list" && row.Index != nil && *row.Index == 0 && row.Value == "500" {
			listedCount = true
		}
	}
	if !listedCount {
		t.Fatal("Device inspection did not use indexed Object_List count")
	}
	pointProperties, err := b.session.properties(ctx, peer, "2:1")
	if err != nil {
		t.Fatal(err)
	}
	failedUnits, keptPV := false, false
	for _, row := range pointProperties.(map[string]any)["rows"].([]propertyRow) {
		if row.Property == "units" && row.Error != "" {
			failedUnits = true
		}
		if row.Property == "present-value" && row.Error == "" {
			keptPV = true
		}
	}
	if !failedUnits || !keptPV {
		t.Fatal("one oversized property prevented other properties from being displayed")
	}
	pageUpdates := 0
	pageContext := context.WithValue(ctx, progressKey{}, func(result any) {
		pageUpdates++
		if len(result.(map[string]any)["objects"].([]map[string]string)) == 0 {
			t.Error("empty object page")
		}
	})
	fast, err := b.session.objectList(pageContext, peer)
	if err != nil {
		t.Fatal(err)
	}
	if len(fast.(map[string]any)["objects"].([]map[string]string)) != 500 || pageUpdates < 8 {
		t.Fatalf("fast inventory: %+v; updates=%d", fast, pageUpdates)
	}
	if _, exists := fast.(map[string]any)["enrichment"]; exists {
		t.Fatal("object browsing performed value enrichment")
	}
	// Clause 6 router announcements must survive the desktop snapshot and
	// retain the routed source identity used to group downstream devices.
	packet, err := npdu.NewNetworkMessagePacket(npdu.IAmRouterToNetwork{Networks: []types.NetworkNumber{2001}})
	if err != nil {
		t.Fatal(err)
	}
	wire, err := packet.AppendTo(nil)
	if err != nil {
		t.Fatal(err)
	}
	// The destination is the desktop listener, not the fixture router itself.
	endpoint, _ := bip.EndpointFromAddrPort(b.session.node.Port().(*bip.Port).LocalAddr())
	mac, _ := endpoint.AppendTo(nil)
	destination, _ := types.NewAddress(0, mac)
	request, err := datalink.NewUnitDataRequest(destination, wire, datalink.PriorityNormal, false, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err = server.Port().Send(ctx, request); err != nil {
		t.Fatal(err)
	}
	for {
		if _, ok := b.session.node.Network().LookupRoute(2001); ok {
			break
		}
		select {
		case <-ctx.Done():
			t.Fatal("router announcement not learned")
		case <-time.After(time.Millisecond):
		}
	}
	routedAddress, _ := types.NewAddress(2001, []byte{9})
	routedPeer, _ := network.NewPeer(routedAddress, 0, peer.peer.NextHop())
	b.session.mu.Lock()
	b.session.peers["routed"] = peerInfo{Key: "routed", Instance: 16, peer: routedPeer}
	b.session.mu.Unlock()
	for _, item := range b.session.devices() {
		if item.Key == peer.Key && (!item.Router || len(item.Networks) != 1 || item.Networks[0] != 2001) {
			t.Fatalf("router identity lost: %+v", item)
		}
		if item.Key == "routed" && (item.RemoteNetwork != 2001 || item.Router) {
			t.Fatalf("routed identity lost: %+v", item)
		}
	}
	b.session.mu.Lock()
	delete(b.session.peers, "routed")
	b.session.mu.Unlock()
	before := server.Application().Stats().ReceivedAPDUs
	if _, err = b.session.discover(ctx, operationArgs{Seconds: 1, RemoteNetwork: 65535}); err != nil {
		t.Fatal(err)
	}
	if server.Application().Stats().ReceivedAPDUs < before+2 {
		t.Fatal("desktop scan did not send both local and global Who-Is")
	}
	inventory, err := b.session.inventory(ctx, peer)
	if err != nil {
		t.Fatal(err)
	}
	if len(inventory.(map[string]any)["objects"].([]map[string]string)) != 500 {
		t.Fatalf("inventory: %+v", inventory)
	}
	if _, err = b.session.properties(ctx, peer, "2:1"); err != nil {
		t.Fatal(err)
	}
	args := operationArgs{Object: "2:1", Property: "present-value", ValueType: "real", Value: "42", Priority: 8}
	if _, err = b.session.write(ctx, peer, args); err == nil {
		t.Fatal("unconfirmed write admitted")
	}
	args.Confirmed = true
	write, err := b.session.write(ctx, peer, args)
	if err != nil {
		t.Fatal(err)
	}
	assertReadback(t, write, "42")
	args.ValueType = "null"
	args.Value = ""
	write, err = b.session.write(ctx, peer, args)
	if err != nil {
		t.Fatal(err)
	}
	assertReadback(t, write, "12")
	if _, err = b.session.read(ctx, peer, []selection{{Object: "2:1", Property: "4194303"}}); err != nil {
		t.Fatalf("property error should remain a result: %v", err)
	}
	if err = b.disconnect(); err != nil {
		t.Fatal(err)
	}
	if _, err = b.dispatch(ctx, rpcRequest{Op: "status"}); err == nil {
		t.Fatal("read after disconnect admitted")
	}
}

func TestExplorerMSTPParticipationAndCancellation(t *testing.T) {
	for _, canceled := range []bool{false, true} {
		t.Run(map[bool]string{false: "sole-master", true: "canceled-startup"}[canceled], func(t *testing.T) {
			bus := virtual.NewBus()
			defer bus.Close()
			wire, err := bus.Connect()
			if err != nil {
				t.Fatal(err)
			}
			master, err := mstp.DefaultMasterConfig(0, 38400)
			if err != nil {
				t.Fatal(err)
			}
			master.MaxMaster = 0
			port, err := mstp.NewPort(mstp.PortConfig{Network: 2001, Master: master}, wire)
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			if canceled {
				cancel()
			}
			b := &backend{}
			_, err = b.connectPort(ctx, connectionConfig{Transport: "mstp", TimeoutMS: 500}, port)
			if canceled {
				if !errors.Is(err, context.Canceled) || b.session != nil {
					t.Fatalf("canceled startup retained session: %v", err)
				}
				if !errors.Is(port.WaitForParticipation(context.Background()), mstp.ErrPortStopped) {
					t.Fatal("canceled port remained active")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			defer b.disconnect()
			if err = port.WaitForParticipation(ctx); err != nil {
				t.Fatal(err)
			}
			if err = b.disconnect(); err != nil {
				t.Fatal(err)
			}
			if !errors.Is(port.WaitForParticipation(ctx), mstp.ErrPortStopped) {
				t.Fatal("disconnected port remained active")
			}
		})
	}
}

func assertReadback(t *testing.T, result any, want string) {
	t.Helper()
	rows := result.(map[string]any)["readback"].(map[string]any)["rows"].([]propertyRow)
	if len(rows) != 1 || rows[0].Value != want || rows[0].Error != "" {
		t.Fatalf("readback: %+v, want %s", rows, want)
	}
}

func TestExplorerDuplicateBindingsAndNotificationBounds(t *testing.T) {
	id, _ := types.NewObjectIdentifier(types.ObjectTypeDevice, 5)
	address, _ := types.NewAddress(0, []byte{1})
	peer, _ := network.NewPeer(address, 2001, address)
	second, _ := types.NewAddress(0, []byte{2})
	other, _ := network.NewPeer(second, 2001, second)
	s := &session{peers: map[string]peerInfo{"a": {Instance: 5, peer: peer}, "b": {Instance: 5, peer: other}}}
	for _, info := range s.devices() {
		if !info.Duplicate {
			t.Fatal("duplicate instance not marked")
		}
	}
	objectID, _ := types.NewObjectIdentifier(types.ObjectTypeAnalogValue, 1)
	for i := 0; i < 300; i++ {
		s.receiveCOV(context.Background(), device.ServerRequest{NetworkPeer: &peer}, service.COVNotification{InitiatingDeviceIdentifier: id, MonitoredObjectIdentifier: objectID, SubscriberProcessIdentifier: 7, Values: []service.PropertyValue{{PropertyIdentifier: types.PropertyPresentValue, Value: encoding.AppendReal(nil, 4)}}}, false)
	}
	if len(s.events) != 256 || s.dropped != 44 || s.sequence != 300 {
		t.Fatalf("notification bounds: %d / %d / %d", len(s.events), s.dropped, s.sequence)
	}
	if s.events[0].Peer != peerKey(id, peer) {
		t.Fatal("COV route lost")
	}
}

func TestExplorerAdmissionAndStrictIPC(t *testing.T) {
	b := &backend{}
	normalized, err := b.dispatch(context.Background(), rpcRequest{Op: "normalize", Args: json.RawMessage("{\"selections\":[{\"object\":\"2:1\",\"property\":\"85\"}]}")})
	if err != nil {
		t.Fatal(err)
	}
	selectors := normalized.([]selection)
	if len(selectors) != 1 || selectors[0].Object != "2:1" || selectors[0].Property != "present-value" {
		t.Fatalf("normalized selectors: %+v", selectors)
	}
	for _, text := range []string{`{"id":1,"op":"system","unexpected":true}`, `{"id":1} {"id":2}`, `null`} {
		var request rpcRequest
		err := decodeStrict([]byte(text), &request)
		if err == nil && request.ID > 0 {
			t.Fatalf("accepted %s", text)
		}
	}
	if _, err := openPort(connectionConfig{Transport: "mstp", Serial: "DO-NOT-OPEN"}); err == nil {
		t.Fatal("MS/TP opened without join authority")
	}
	if _, err := parseSelection(selection{Object: "2:4194304", Property: "present-value"}); err == nil {
		t.Fatal("invalid object accepted")
	}
	var output bytes.Buffer
	if err := serve(context.Background(), strings.NewReader("{\"id\":1,\"op\":\"status\",\"args\":{}}\n"), &output); err != nil {
		t.Fatal(err)
	}
	var response rpcResponse
	if err := json.Unmarshal(output.Bytes(), &response); err != nil || response.ID != 1 || !strings.Contains(response.Error, "connect") {
		t.Fatalf("response: %s", output.String())
	}
}
