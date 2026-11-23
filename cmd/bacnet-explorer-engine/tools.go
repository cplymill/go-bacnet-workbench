package main

import (
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"net/netip"
	"time"

	"github.com/cplymill/go-bacnet/datalink/bip"
	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/propertyvalue"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/transaction"
	"github.com/cplymill/go-bacnet/types"
)

func (s *session) createObject(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed || len(args.InitialValues) > 64 {
		return nil, errors.New("object creation requires confirmation and at most 64 initial values")
	}
	request := service.CreateObjectRequest{}
	if args.AllocateInstance {
		request.Specifier = service.CreateObjectSpecifier{Kind: service.CreateObjectByType, ObjectType: types.ObjectType(args.ObjectType)}
	} else {
		id, err := types.ParseObjectIdentifier(args.Object)
		if err != nil {
			return nil, err
		}
		request.Specifier = service.CreateObjectSpecifier{Kind: service.CreateObjectByIdentifier, ObjectIdentifier: id}
	}
	for _, item := range args.InitialValues {
		property, err := types.ParsePropertyIdentifier(item.Property)
		if err != nil {
			return nil, err
		}
		value, err := propertyvalue.Encode(item.ValueType, item.Value)
		if err != nil {
			return nil, err
		}
		index := service.ArrayIndex{}
		if item.Index != nil {
			index = service.ArrayIndex{Present: true, Value: *item.Index}
		}
		request.InitialValues = append(request.InitialValues, service.PropertyValue{PropertyIdentifier: property, ArrayIndex: index, Value: value})
	}
	id, err := s.node.CreateObjectWithCapabilities(ctx, peer.peer, capabilities(peer), request, service.MultipleLimits{})
	if err != nil {
		return nil, err
	}
	return map[string]any{"object": objectText(id), "acknowledged": true}, nil
}

func (s *session) deleteObject(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed {
		return nil, errors.New("object deletion requires confirmation")
	}
	id, err := types.ParseObjectIdentifier(args.Object)
	if err != nil {
		return nil, err
	}
	if id.Type() == types.ObjectTypeDevice {
		return nil, errors.New("select a non-Device object for deletion")
	}
	if err = s.node.DeleteObject(ctx, peer.peer, service.DeleteObjectRequest{ObjectIdentifier: id}); err != nil {
		return nil, err
	}
	return map[string]any{"object": args.Object, "acknowledged": true}, nil
}

func (s *session) communicationControl(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed || args.State != uint32(service.CommunicationEnable) && args.State != uint32(service.CommunicationDisableInitiation) {
		return nil, errors.New("confirm Enable or Disable Initiation communication control")
	}
	request := service.DeviceCommunicationControlRequest{EnableDisable: service.CommunicationEnableDisable(args.State), TimeDuration: args.Minutes}
	if args.Password != "" {
		password, err := types.NewUTF8String(args.Password)
		if err != nil {
			return nil, err
		}
		request.Password = &password
	}
	if err := s.node.DeviceCommunicationControl(ctx, peer.peer, request, encoding.Limits{}); err != nil {
		return nil, err
	}
	return map[string]any{"acknowledged": true, "state": args.State, "minutes": args.Minutes}, nil
}

func (s *session) routers(ctx context.Context) (any, error) {
	if err := s.node.Network().SendWhoIsRouterToNetwork(ctx, s.node.LinkNetwork(), nil); err != nil {
		return nil, err
	}
	timer := time.NewTimer(3 * time.Second)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-timer.C:
	}
	rows := []map[string]any{}
	for _, route := range s.node.Network().Routes() {
		rows = append(rows, map[string]any{"network": route.Network, "linkNetwork": route.LinkNetwork, "nextHop": hex.EncodeToString(route.NextHop.MAC()), "status": route.Status, "updated": route.UpdatedAt, "busyUntil": route.BusyUntil})
	}
	return rows, nil
}

func (s *session) bbmd(ctx context.Context, target string) (any, error) {
	port, ok := s.node.Port().(*bip.Port)
	if !ok {
		return nil, errors.New("BBMD table inspection requires IPv4")
	}
	endpoint, err := netip.ParseAddrPort(target)
	if err != nil {
		return nil, err
	}
	bbmd, err := bip.EndpointFromAddrPort(endpoint)
	if err != nil {
		return nil, err
	}
	client, err := bip.ListenManagementClient(bip.ManagementClientConfig{Local: netip.AddrPortFrom(port.LocalAddr().Addr(), 0)})
	if err != nil {
		return nil, err
	}
	defer client.Close()
	bounded, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	bdt, bdtErr := client.ReadBroadcastDistributionTable(bounded, bbmd)
	fdt, fdtErr := client.ReadForeignDeviceTable(bounded, bbmd)
	bdtRows := []map[string]any{}
	for _, entry := range bdt {
		bdtRows = append(bdtRows, map[string]any{"address": entry.Address.AddrPort().String(), "mask": entry.BroadcastMask.String()})
	}
	fdtRows := []map[string]any{}
	for _, entry := range fdt {
		fdtRows = append(fdtRows, map[string]any{"address": entry.Address.AddrPort().String(), "ttl": entry.TTL, "remaining": entry.Remaining})
	}
	result := map[string]any{"bdt": bdtRows, "fdt": fdtRows}
	if bdtErr != nil {
		result["bdtError"] = bdtErr.Error()
	}
	if fdtErr != nil {
		result["fdtError"] = fdtErr.Error()
	}
	return result, nil
}

func capabilities(peer peerInfo) transaction.PeerCapabilities {
	return transaction.PeerCapabilities{MaxAPDULengthAccepted: peer.MaxAPDU, CanReceiveSegmentation: peer.Segmentation == fmt.Sprint(service.SegmentationBoth) || peer.Segmentation == fmt.Sprint(service.SegmentationReceive)}
}

func (s *session) readFile(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if args.Offset < 0 || args.FileCount < 1 || args.FileCount > 4096 {
		return nil, errors.New("file offset must be >=0; block count must be 1..4096")
	}
	id, err := types.ParseObjectIdentifier(args.Object)
	if err != nil {
		return nil, err
	}
	method := service.FileAccessStream
	if args.RecordMode {
		method = service.FileAccessRecord
		if args.FileCount > 64 {
			return nil, errors.New("record count must be 1..64")
		}
	}
	ack, err := s.node.AtomicReadFile(ctx, peer.peer, service.AtomicReadFileRequest{FileIdentifier: id, AccessMethod: method, Start: args.Offset, Count: args.FileCount}, service.FileLimits{MaxRecords: 64, Encoding: encoding.Limits{MaxInputLength: 65536, MaxValueLength: 65536}})
	if err != nil {
		return nil, err
	}
	return map[string]any{"eof": ack.EndOfFile, "offset": ack.Start, "data": ack.Data, "records": ack.Records}, nil
}

func (s *session) writeFile(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed {
		return nil, errors.New("file write requires operator confirmation")
	}
	if args.Offset < 0 || len(args.Data) > 4096 || len(args.Records) > 64 {
		return nil, errors.New("file writes require nonnegative offset, <=4096 bytes or <=64 records")
	}
	id, err := types.ParseObjectIdentifier(args.Object)
	if err != nil {
		return nil, err
	}
	method := service.FileAccessStream
	if args.RecordMode {
		method = service.FileAccessRecord
	}
	ack, err := s.node.AtomicWriteFileWithCapabilities(ctx, peer.peer, capabilities(peer), service.AtomicWriteFileRequest{FileIdentifier: id, AccessMethod: method, Start: args.Offset, Data: args.Data, Records: args.Records}, service.FileLimits{MaxRecords: 64, Encoding: encoding.Limits{MaxInputLength: 65536, MaxValueLength: 65536}})
	if err != nil {
		return nil, err
	}
	if ack.Start != args.Offset {
		return nil, errors.New("file write acknowledged at an unexpected offset; inspect the peer file before retrying")
	}
	return map[string]any{"acknowledged": true, "offset": ack.Start}, nil
}

func (s *session) acknowledge(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed {
		return nil, errors.New("alarm acknowledgment requires operator confirmation")
	}
	id, err := types.ParseObjectIdentifier(args.Object)
	if err != nil {
		return nil, err
	}
	source, err := types.NewUTF8String(args.Source)
	if err != nil {
		return nil, err
	}
	if source.Len() < 1 || source.Len() > 128 {
		return nil, errors.New("acknowledgment source must be 1..128 bytes")
	}
	now := time.Now()
	date, err := types.DateFromTime(now)
	if err != nil {
		return nil, err
	}
	return true, s.node.AcknowledgeAlarm(ctx, peer.peer, service.AcknowledgeAlarmRequest{AcknowledgingProcessIdentifier: 1, EventObjectIdentifier: id, EventStateAcknowledged: types.EventState(args.EventState), TimeStamp: args.EventTime, AcknowledgmentSource: source, TimeOfAcknowledgment: service.TimeStamp{Kind: service.TimeStampDateTime, DateTime: types.DateTime{Date: date, Time: types.TimeOfDayFromTime(now)}}}, encoding.Limits{})
}

func (s *session) timeSync(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed {
		return nil, errors.New("time synchronization requires operator confirmation")
	}
	now := time.Now().UTC()
	date, err := types.DateFromTime(now)
	if err != nil {
		return nil, err
	}
	payload, err := service.AppendUTCTimeSynchronization(nil, service.TimeSynchronizationRequest{Date: date, Time: types.TimeOfDayFromTime(now)})
	if err != nil {
		return nil, err
	}
	return map[string]any{"sentUTC": now, "unconfirmed": true}, s.node.SendUnconfirmed(ctx, peer.peer, payload)
}

func (s *session) restart(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed || args.State > 1 {
		return nil, errors.New("explicitly confirmed warm or cold restart only")
	}
	request := service.ReinitializeDeviceRequest{State: service.ReinitializedState(args.State)}
	if args.Password != "" {
		password, err := types.NewUTF8String(args.Password)
		if err != nil {
			return nil, err
		}
		request.Password = &password
	}
	return true, s.node.ReinitializeDevice(ctx, peer.peer, request, encoding.Limits{})
}
