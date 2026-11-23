package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"net/netip"
	"testing"
	"time"

	"github.com/cplymill/go-bacnet/datalink/bip"
	"github.com/cplymill/go-bacnet/device"
	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/object"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/transaction"
	"github.com/cplymill/go-bacnet/types"
)

// Desktop composition coverage for Clauses 14, 15.1, 15.3 and 16.4/16.8/16.9.
// All transport and service processing uses the production stack.
func TestExplorerServiceToolsLoopback(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	id, _ := types.NewObjectIdentifier(types.ObjectTypeDevice, 992)
	analogID, _ := types.NewObjectIdentifier(types.ObjectTypeAnalogValue, 1)
	fileID, _ := types.NewObjectIdentifier(types.ObjectTypeFile, 1)
	name, _ := types.NewUTF8String("Tool fixture")
	calendarID, _ := types.NewObjectIdentifier(types.ObjectTypeCalendar, 1)
	calendar, err := object.NewCalendar(object.CalendarConfig{Identifier: calendarID, Name: name, Writable: true})
	if err != nil {
		t.Fatal(err)
	}
	scheduleID, _ := types.NewObjectIdentifier(types.ObjectTypeSchedule, 1)
	pattern := types.Date{Year: 255, Month: 255, Day: 255, Weekday: 255}
	schedule, err := object.NewSchedule(object.ScheduleConfig{Identifier: scheduleID, Name: name, Writable: true, PriorityForWriting: 8, ScheduleDefault: encoding.AppendBoolean(nil, false), EffectivePeriod: types.DateRange{StartDate: pattern, EndDate: pattern}, ResolveCalendar: func(_ context.Context, _ types.ObjectIdentifier, date types.Date) (bool, error) {
		return calendar.Matches(date)
	}})
	if err != nil {
		t.Fatal(err)
	}
	analog, err := object.NewAnalogValue(object.AnalogValueConfig{Identifier: analogID, Name: name, RelinquishDefault: 5})
	if err != nil {
		t.Fatal(err)
	}
	registry := device.NewObjectRegistry()
	if err := registry.Register(analogID, analog); err != nil {
		t.Fatal(err)
	}
	if err := registry.Register(scheduleID, schedule); err != nil {
		t.Fatal(err)
	}
	if err = registry.Register(calendarID, calendar); err != nil {
		t.Fatal(err)
	}
	file, err := object.NewFile(object.FileConfig{Identifier: fileID, Name: name, FileType: name, InitialData: []byte("abcdef"), MaxSize: 1024})
	if err != nil {
		t.Fatal(err)
	}
	var server *device.DataLinkNode
	cov, err := device.NewCOVManager(device.COVManagerConfig{InitiatingDeviceIdentifier: id, Send: func(ctx context.Context, target device.COVNotificationTarget, confirmed bool, value service.COVNotification) error {
		return server.SendCOVNotification(ctx, target, confirmed, value, service.COVLimits{})
	}})
	if err != nil {
		t.Fatal(err)
	}
	cov.RegisterObject(analogID, analog)
	acknowledgments := make(chan service.AcknowledgeAlarmRequest, 1)
	restarts := make(chan service.ReinitializeDeviceRequest, 1)
	clock := make(chan bool, 1)
	creations := make(chan service.CreateObjectRequest, 1)
	deletions := make(chan service.DeleteObjectRequest, 1)
	controls := make(chan service.DeviceCommunicationControlRequest, 1)
	timestamp := service.TimeStamp{Kind: service.TimeStampSequenceNumber, SequenceNumber: 7}
	summary := service.EventSummary{ObjectIdentifier: analogID, EventState: types.EventStateOffnormal, EventTimeStamps: [3]service.TimeStamp{timestamp, timestamp, timestamp}, EventPriorities: [3]uint32{10, 20, 30}}
	handler := device.ApplicationServerHandler{
		ObjectCreator: device.CreateObjectExecutorFunc(func(_ context.Context, request service.CreateObjectRequest) (types.ObjectIdentifier, error) {
			creations <- request
			if request.Specifier.Kind == service.CreateObjectByIdentifier {
				return request.Specifier.ObjectIdentifier, nil
			}
			return types.NewObjectIdentifier(request.Specifier.ObjectType, 7)
		}),
		ObjectDeleter: device.DeleteObjectExecutorFunc(func(_ context.Context, request service.DeleteObjectRequest) error {
			deletions <- request
			return nil
		}),
		Communication: device.DeviceCommunicationControlFunc(func(_ context.Context, _ device.IPServerRequest, request service.DeviceCommunicationControlRequest) error {
			controls <- request
			return nil
		}),
		IAm: service.IAm{DeviceIdentifier: id, MaxAPDU: 1476, Segmentation: service.SegmentationBoth, VendorID: 999},
		Properties: device.PropertyExecutorFuncs{ReadFunc: registry.ReadProperty, WriteFunc: registry.WriteProperty, ListFunc: func(ctx context.Context, id types.ObjectIdentifier, selector types.PropertyIdentifier) ([]types.PropertyIdentifier, error) {
			if selector == types.PropertyAll {
				return nil, service.ProtocolError{Class: service.ErrorClassProperty, Code: service.ErrorCodeUnknownProperty}
			}
			return registry.PropertiesForSelector(ctx, id, selector)
		}},
		Files: file, COV: cov,
		AlarmAcknowledgments: device.AcknowledgeAlarmExecutorFunc(func(_ context.Context, _ device.IPServerRequest, request service.AcknowledgeAlarmRequest) error {
			acknowledgments <- request
			return nil
		}),
		EventInformation: device.GetEventInformationExecutorFunc(func(_ context.Context, _ device.IPServerRequest, _ service.GetEventInformationRequest) (service.GetEventInformationResult, error) {
			return service.GetEventInformationResult{Summaries: []service.EventSummary{summary}}, nil
		}),
		ReinitializeDevice: device.ReinitializeDeviceFunc(func(_ context.Context, _ device.IPServerRequest, request service.ReinitializeDeviceRequest) error {
			restarts <- request
			return nil
		}),
		TimeSynchronizer: device.TimeSynchronizerFunc(func(_ context.Context, _ device.IPServerRequest, _ service.TimeSynchronizationRequest, utc bool) {
			clock <- utc
		}),
	}
	port, err := bip.ListenNormalPort(bip.PortConfig{Local: netip.MustParseAddrPort("127.0.0.1:0"), Broadcast: netip.MustParseAddrPort("127.0.0.1:47808")})
	if err != nil {
		t.Fatal(err)
	}
	server, err = device.NewDataLinkNode(port, device.DataLinkNodeConfig{Application: transaction.ApplicationConfig{Server: handler}})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- server.Run(ctx) }()
	t.Cleanup(func() { cancel(); _ = server.Close(); <-done })
	for !server.Running() {
		select {
		case <-ctx.Done():
			t.Fatal(ctx.Err())
		case <-time.After(time.Millisecond):
		}
	}
	b := &backend{}
	t.Cleanup(func() { _ = b.disconnect() })
	if _, err = b.connect(ctx, connectionConfig{Transport: "ip", Bind: "127.0.0.1:0", Broadcast: "127.0.0.1:47808", TimeoutMS: 500}); err != nil {
		t.Fatal(err)
	}
	found, err := b.session.discover(ctx, operationArgs{Target: port.LocalAddr().String(), Seconds: 1})
	if err != nil {
		t.Fatal(err)
	}
	peers := found.([]peerInfo)
	if len(peers) != 1 {
		t.Fatalf("found: %+v", peers)
	}
	peer := peers[0]
	s := b.session
	inspected, err := s.properties(ctx, peer, "2:1")
	if err != nil {
		t.Fatal(err)
	}
	full := inspected.(map[string]any)
	if full["fallbackReason"] == nil {
		t.Fatal("ALL-selector fallback not recorded")
	}
	foundPriority := false
	for _, row := range full["rows"].([]propertyRow) {
		if row.Property == "current-command-priority" && row.Error == "" {
			foundPriority = true
		}
	}
	if !foundPriority {
		t.Fatalf("Property_List fallback omitted advertised property: %+v", full)
	}
	loadedSchedule, err := s.readSchedule(ctx, peer, "17:1")
	if err != nil {
		t.Fatal(err)
	}
	loaded := loadedSchedule.(map[string]any)
	days := loaded["days"].([][]scheduleEntry)
	if len(days) != 7 || loaded["defaultType"] != "boolean" || loaded["defaultValue"] != "false" {
		t.Fatalf("schedule read: %+v", loaded)
	}
	days[0] = []scheduleEntry{{Time: "08:00", Type: "boolean", Value: "true"}, {Time: "17:00", Type: "boolean", Value: "false"}}
	weeklyJSON, _ := json.Marshal(days)
	if _, err = s.writeSchedule(ctx, peer, operationArgs{Object: "17:1", ExpectedRaw: loaded["raw"].(string), Value: string(weeklyJSON), Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	freshSchedule, err := s.readSchedule(ctx, peer, "17:1")
	if err != nil {
		t.Fatal(err)
	}
	fresh := freshSchedule.(map[string]any)
	if len(fresh["days"].([][]scheduleEntry)[0]) != 2 {
		t.Fatal("saved schedule transitions lost")
	}
	if _, err = s.writeSchedule(ctx, peer, operationArgs{Object: "17:1", ExpectedRaw: loaded["raw"].(string), Value: string(weeklyJSON), Confirmed: true}); err == nil {
		t.Fatal("stale schedule save accepted")
	}
	if _, err = s.writeSchedule(ctx, peer, operationArgs{Object: "17:1", ExpectedRaw: fresh["raw"].(string), Value: string(weeklyJSON)}); err == nil {
		t.Fatal("unconfirmed schedule save accepted")
	}
	if _, err = s.writeSchedule(ctx, peer, operationArgs{Object: "17:1", ExpectedRaw: fresh["raw"].(string), Value: string(weeklyJSON), DefaultType: "boolean", DefaultValue: "true", ExpectedDefaultRaw: fresh["defaultRaw"].(string), Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	defaultReadback, err := s.readSchedule(ctx, peer, "17:1")
	if err != nil || defaultReadback.(map[string]any)["defaultValue"] != "true" {
		t.Fatalf("default readback: %+v %v", defaultReadback, err)
	}
	if _, err = s.writeSchedule(ctx, peer, operationArgs{Object: "17:1", ExpectedRaw: fresh["raw"].(string), Value: string(weeklyJSON), DefaultType: "boolean", DefaultValue: "false", ExpectedDefaultRaw: fresh["defaultRaw"].(string), Confirmed: true}); err == nil {
		t.Fatal("stale default save accepted")
	}
	calendarRead, err := s.readCalendar(ctx, peer, "6:1")
	if err != nil {
		t.Fatal(err)
	}
	calendarRaw := calendarRead.(map[string]any)["raw"].(string)
	rules := []calendarRule{{Kind: "date", Start: "*-12-25", Weekday: "*"}, {Kind: "range", Start: "2026-11-02", End: "2026-11-06"}, {Kind: "week", Month: "*", Week: "6", Weekday: "Fri"}}
	if _, err = s.writeCalendar(ctx, peer, operationArgs{Object: "6:1", Rules: rules, ExpectedListRaw: &calendarRaw, Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	calendarRead, err = s.readCalendar(ctx, peer, "6:1")
	if err != nil {
		t.Fatal(err)
	}
	if len(calendarRead.(map[string]any)["rules"].([]calendarRule)) != 3 {
		t.Fatal("calendar rules lost")
	}
	if _, err = s.writeCalendar(ctx, peer, operationArgs{Object: "6:1", Rules: []calendarRule{}, ExpectedListRaw: &calendarRaw, Confirmed: true}); err == nil {
		t.Fatal("stale calendar save accepted")
	}
	calendarRaw = calendarRead.(map[string]any)["raw"].(string)
	if _, err = s.writeCalendar(ctx, peer, operationArgs{Object: "6:1", Rules: []calendarRule{}, ExpectedListRaw: &calendarRaw, Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	calendarRead, err = s.readCalendar(ctx, peer, "6:1")
	if err != nil || len(calendarRead.(map[string]any)["rules"].([]calendarRule)) != 0 {
		t.Fatalf("clear calendar: %v", err)
	}
	currentSchedule := defaultReadback.(map[string]any)
	exceptionsRaw := currentSchedule["exceptionsRaw"].(string)
	periodRaw := currentSchedule["periodRaw"].(string)
	exceptions := []scheduleException{{Rule: calendarRule{Kind: "date", Start: "*-12-25", Weekday: "*"}, Priority: 8, Times: []scheduleEntry{{Time: "00:00", Type: "boolean", Value: "false"}}}, {CalendarReference: "6:1", Priority: 9, Times: []scheduleEntry{{Time: "08:00", Type: "boolean", Value: "true"}, {Time: "17:00", Type: "null", Value: ""}}}}
	if _, err = s.writeSchedule(ctx, peer, operationArgs{Object: "17:1", ExpectedRaw: currentSchedule["raw"].(string), Value: string(weeklyJSON), Exceptions: exceptions, ExpectedExceptionsRaw: &exceptionsRaw, PeriodStart: "2026-01-01", PeriodEnd: "2026-12-31", ExpectedPeriodRaw: &periodRaw, Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	completeSchedule, err := s.readSchedule(ctx, peer, "17:1")
	if err != nil {
		t.Fatal(err)
	}
	complete := completeSchedule.(map[string]any)
	if len(complete["exceptions"].([]scheduleException)) != 2 || complete["periodStart"] != "2026-01-01" {
		t.Fatalf("schedule exceptions/period readback: %+v", complete)
	}
	if _, err = s.writeSchedule(ctx, peer, operationArgs{Object: "17:1", ExpectedRaw: currentSchedule["raw"].(string), Value: string(weeklyJSON), Exceptions: exceptions, ExpectedExceptionsRaw: &exceptionsRaw, Confirmed: true}); err == nil {
		t.Fatal("stale exception save accepted")
	}
	for _, allocated := range []bool{false, true} {
		created, err := s.createObject(ctx, peer, operationArgs{Confirmed: true, Object: "2:7", ObjectType: 2, AllocateInstance: allocated, InitialValues: []initialValue{{Property: "object-name", ValueType: "character-string", Value: "Created fixture"}}})
		if err != nil || created.(map[string]any)["object"] != "2:7" {
			t.Fatalf("create: %+v %v", created, err)
		}
		request := <-creations
		if len(request.InitialValues) != 1 || request.InitialValues[0].PropertyIdentifier != types.PropertyObjectName {
			t.Fatalf("initial values lost: %+v", request)
		}
	}
	if _, err = s.deleteObject(ctx, peer, operationArgs{Confirmed: true, Object: "2:7"}); err != nil {
		t.Fatal(err)
	}
	if request := <-deletions; request.ObjectIdentifier.Instance() != 7 {
		t.Fatal("deletion target changed")
	}
	minutes := uint16(5)
	if _, err = s.communicationControl(ctx, peer, operationArgs{Confirmed: true, State: 2, Minutes: &minutes, Password: "fixture"}); err != nil {
		t.Fatal(err)
	}
	if request := <-controls; request.TimeDuration == nil || *request.TimeDuration != 5 || request.EnableDisable != service.CommunicationDisableInitiation || request.Password == nil {
		t.Fatalf("communication control changed: %+v", request)
	}
	if _, err = s.createObject(ctx, peer, operationArgs{}); err == nil {
		t.Fatal("unconfirmed create admitted")
	}
	if _, err = s.deleteObject(ctx, peer, operationArgs{Confirmed: true, Object: "8:992"}); err == nil {
		t.Fatal("Device deletion admitted")
	}
	if _, err = s.communicationControl(ctx, peer, operationArgs{Confirmed: true, State: 1}); err == nil {
		t.Fatal("deprecated communication mode admitted")
	}
	if _, err = s.subscribe(ctx, peer, operationArgs{Object: "2:1", Process: 7, Lifetime: 30}); err != nil {
		t.Fatal(err)
	}
	for {
		s.mu.Lock()
		count := len(s.events)
		s.mu.Unlock()
		if count > 0 {
			break
		}
		select {
		case <-ctx.Done():
			t.Fatal("missing COV notification")
		case <-time.After(time.Millisecond):
		}
	}
	if _, err = s.subscribe(ctx, peer, operationArgs{Object: "2:1", Process: 7}); err != nil {
		t.Fatal(err)
	}
	if cov.ActiveCount() != 0 {
		t.Fatal("COV cancellation was not delivered")
	}
	block, err := s.readFile(ctx, peer, operationArgs{Object: "10:1", FileCount: 6})
	if err != nil {
		t.Fatal(err)
	}
	if string(block.(map[string]any)["data"].([]byte)) != "abcdef" {
		t.Fatalf("file: %+v", block)
	}
	if _, err = s.writeFile(ctx, peer, operationArgs{Object: "10:1", Offset: 2, Data: []byte("XY")}); err == nil {
		t.Fatal("unconfirmed file write admitted")
	}
	if _, err = s.writeFile(ctx, peer, operationArgs{Object: "10:1", Offset: 2, Data: []byte("XY"), Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	block, err = s.readFile(ctx, peer, operationArgs{Object: "10:1", FileCount: 6})
	if err != nil {
		t.Fatal(err)
	}
	if string(block.(map[string]any)["data"].([]byte)) != "abXYef" {
		t.Fatalf("file readback: %+v", block)
	}
	events, err := s.eventInformation(ctx, peer)
	if err != nil {
		t.Fatal(err)
	}
	if len(events.([]map[string]any)) != 1 {
		t.Fatalf("events: %+v", events)
	}
	if _, err = s.acknowledge(ctx, peer, operationArgs{Object: "2:1", EventState: 2, EventTime: timestamp, Source: "Test operator", Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	select {
	case request := <-acknowledgments:
		if request.TimeStamp != timestamp {
			t.Fatal("alarm timestamp changed")
		}
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	if _, err = s.timeSync(ctx, peer, operationArgs{Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	select {
	case utc := <-clock:
		if !utc {
			t.Fatal("UTC flag lost")
		}
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	if _, err = s.restart(ctx, peer, operationArgs{State: 1, Confirmed: true}); err != nil {
		t.Fatal(err)
	}
	select {
	case request := <-restarts:
		if request.State != service.ReinitializeWarmStart {
			t.Fatal("restart state changed")
		}
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	read, err := s.read(ctx, peer, []selection{{Object: "2:1", Property: "present-value"}})
	if err != nil {
		t.Fatal(err)
	}
	rows := read.(map[string]any)["rows"].([]propertyRow)
	wire, _, err := encoding.DecodeReal(mustHex(t, rows[0].Raw), encoding.Limits{})
	if err != nil || wire != 5 {
		t.Fatalf("post-tool read: %+v", rows)
	}
}

func mustHex(t *testing.T, text string) []byte {
	t.Helper()
	result, err := hex.DecodeString(text)
	if err != nil {
		t.Fatal(err)
	}
	return result
}
