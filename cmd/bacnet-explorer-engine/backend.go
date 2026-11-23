package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"runtime"
	"sort"
	"sync"
	"time"

	"github.com/cplymill/go-bacnet/datalink"
	"github.com/cplymill/go-bacnet/datalink/bip"
	"github.com/cplymill/go-bacnet/datalink/bip6"
	"github.com/cplymill/go-bacnet/datalink/mstp"
	"github.com/cplymill/go-bacnet/datalink/mstp/serialport"
	"github.com/cplymill/go-bacnet/device"
	"github.com/cplymill/go-bacnet/encoding"
	"github.com/cplymill/go-bacnet/network"
	"github.com/cplymill/go-bacnet/propertyvalue"
	"github.com/cplymill/go-bacnet/remote"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/transaction"
	"github.com/cplymill/go-bacnet/types"
	"go.bug.st/serial"
)

func bytesReader(data []byte) *bytes.Reader { return bytes.NewReader(data) }

type connectionConfig struct {
	Transport        string `json:"transport"`
	Bind             string `json:"bind"`
	Broadcast        string `json:"broadcast"`
	Serial           string `json:"serial"`
	Baud             int    `json:"baud"`
	MAC              int    `json:"mac"`
	MaxMaster        int    `json:"maxMaster"`
	Network          uint16 `json:"network"`
	TimeoutMS        int    `json:"timeoutMs"`
	Retries          int    `json:"retries"`
	ConfirmJoin      bool   `json:"confirmJoin"`
	BBMD             string `json:"bbmd"`
	TTL              uint16 `json:"ttl"`
	IPv6Bind         string `json:"ipv6Bind"`
	InterfaceIndex   int    `json:"interfaceIndex"`
	VMAC             string `json:"vmac"`
	Hub              string `json:"hub"`
	FailoverHub      string `json:"failoverHub"`
	Certificate      string `json:"certificate"`
	PrivateKey       string `json:"privateKey"`
	SiteCA           string `json:"siteCa"`
	UUID             string `json:"uuid"`
	VerifyServerName bool   `json:"verifyServerName"`
}

type peerInfo struct {
	Key           string    `json:"key"`
	Device        string    `json:"device"`
	Name          string    `json:"name"`
	Instance      uint32    `json:"instance"`
	Address       string    `json:"address"`
	Route         string    `json:"route"`
	Vendor        uint16    `json:"vendor"`
	MaxAPDU       int       `json:"maxApdu"`
	Segmentation  string    `json:"segmentation"`
	Seen          time.Time `json:"seen"`
	Duplicate     bool      `json:"duplicate"`
	Router        bool      `json:"router"`
	Networks      []uint16  `json:"networks"`
	RemoteNetwork uint16    `json:"remoteNetwork"`
	peer          network.Peer
}

type propertyRow struct {
	Device      string    `json:"device"`
	Object      string    `json:"object"`
	Property    string    `json:"property"`
	Index       *uint32   `json:"index,omitempty"`
	Value       string    `json:"value"`
	Raw         string    `json:"raw"`
	Type        string    `json:"type"`
	EditorValue string    `json:"editorValue"`
	EditorType  string    `json:"editorType"`
	ArrayValues []string  `json:"arrayValues,omitempty"`
	ArrayTypes  []string  `json:"arrayTypes,omitempty"`
	Elements    []string  `json:"elements,omitempty"`
	Error       string    `json:"error"`
	At          time.Time `json:"at"`
}

type notification struct {
	Sequence uint64        `json:"sequence"`
	Peer     string        `json:"peer"`
	Process  uint32        `json:"process"`
	Rows     []propertyRow `json:"rows"`
}

type session struct {
	node     *device.DataLinkNode
	cancel   context.CancelFunc
	done     chan struct{}
	mu       sync.Mutex
	peers    map[string]peerInfo
	events   []notification
	traffic  []map[string]any
	sequence uint64
	dropped  uint64
	err      error
}

type backend struct{ session *session }

func (b *backend) dispatch(ctx context.Context, request rpcRequest) (any, error) {
	switch request.Op {
	case "normalize":
		var args operationArgs
		if err := decodeStrict(request.Args, &args); err != nil {
			return nil, err
		}
		if len(args.Selections) > 64 {
			return nil, errors.New("normalization is limited to 64 selectors")
		}
		result := []selection{}
		for _, item := range args.Selections {
			parsed, err := parseSelection(item)
			if err != nil {
				return nil, err
			}
			result = append(result, selection{Object: objectText(parsed.ObjectIdentifier), Property: parsed.PropertyIdentifier.String(), Index: item.Index})
		}
		return result, nil
	case "unit-choices":
		return engineeringUnitChoices(), nil
	case "system":
		interfaces, err := net.Interfaces()
		if err != nil {
			return nil, err
		}
		addresses := []string{}
		profiles := []map[string]string{}
		for _, iface := range interfaces {
			if iface.Flags&net.FlagUp == 0 {
				continue
			}
			list, err := iface.Addrs()
			if err != nil {
				continue
			}
			for _, address := range list {
				prefix, err := netip.ParsePrefix(address.String())
				if err == nil && prefix.Addr().Is4() {
					addresses = append(addresses, prefix.Addr().String()+":47808")
					mask := net.CIDRMask(prefix.Bits(), 32)
					ip := prefix.Addr().As4()
					for i := range ip {
						ip[i] |= ^mask[i]
					}
					if !prefix.Addr().IsLoopback() && prefix.Bits() < 31 {
						profiles = append(profiles, map[string]string{"bind": prefix.Addr().String() + ":47808", "broadcast": netip.AddrFrom4(ip).String() + ":47808"})
					}
				}
			}
		}
		ports, err := serial.GetPortsList()
		if err != nil {
			ports = []string{}
		}
		return map[string]any{"interfaces": addresses, "interfaceProfiles": profiles, "serialPorts": ports, "version": "0.2.20"}, nil
	case "connect":
		var config connectionConfig
		if err := decodeStrict(request.Args, &config); err != nil {
			return nil, err
		}
		return b.connect(ctx, config)
	case "disconnect":
		return true, b.disconnect()
	}
	s := b.session
	if s == nil {
		return nil, errors.New("connect to a BACnet link first")
	}
	if !s.node.Running() {
		return nil, errors.New("BACnet connection stopped; disconnect and reconnect")
	}
	var args operationArgs
	if len(request.Args) > 0 {
		if err := decodeStrict(request.Args, &args); err != nil {
			return nil, err
		}
	}
	switch request.Op {
	case "discover":
		return s.discover(ctx, args)
	case "devices":
		return s.devices(), nil
	case "status":
		result := map[string]any{"application": s.node.Application().Stats(), "network": s.node.Network().Stats()}
		if port, ok := s.node.Port().(*mstp.Port); ok {
			result["mstp"] = port.Stats()
		}
		if port, ok := s.node.Port().(*bip.Port); ok {
			result["ipv4"] = port.Stats()
		}
		if port, ok := s.node.Port().(*bip6.Port); ok {
			result["ipv6"] = port.Stats()
		}
		return result, nil
	case "routers":
		return s.routers(ctx)
	case "bbmd":
		return s.bbmd(ctx, args.Target)
	case "traffic":
		s.mu.Lock()
		defer s.mu.Unlock()
		return map[string]any{"rows": s.traffic, "received": s.node.Application().Stats().ReceivedAPDUs, "dropped": s.node.Application().Stats().DroppedObservations}, nil
	case "notifications":
		s.mu.Lock()
		defer s.mu.Unlock()
		events := []notification{}
		for _, event := range s.events {
			if event.Sequence > args.After {
				events = append(events, event)
			}
		}
		return map[string]any{"events": events, "dropped": s.dropped, "sequence": s.sequence}, nil
	}
	peer, err := s.lookup(args.Device)
	if err != nil {
		return nil, err
	}
	switch request.Op {
	case "inventory":
		return s.inventory(ctx, peer)
	case "object-list":
		return s.objectList(ctx, peer)
	case "properties":
		return s.properties(ctx, peer, args.Object)
	case "read":
		return s.read(ctx, peer, args.Selections)
	case "write":
		return s.write(ctx, peer, args)
	case "subscribe":
		return s.subscribe(ctx, peer, args)
	case "events":
		return s.eventInformation(ctx, peer)
	case "range":
		return s.readRange(ctx, peer, args)
	case "file-read":
		return s.readFile(ctx, peer, args)
	case "file-write":
		return s.writeFile(ctx, peer, args)
	case "acknowledge":
		return s.acknowledge(ctx, peer, args)
	case "time-sync":
		return s.timeSync(ctx, peer, args)
	case "restart":
		return s.restart(ctx, peer, args)
	case "create-object":
		return s.createObject(ctx, peer, args)
	case "delete-object":
		return s.deleteObject(ctx, peer, args)
	case "communication-control":
		return s.communicationControl(ctx, peer, args)
	case "schedule-read":
		return s.readSchedule(ctx, peer, args.Object)
	case "schedule-write":
		return s.writeSchedule(ctx, peer, args)
	case "calendar-read":
		return s.readCalendar(ctx, peer, args.Object)
	case "calendar-write":
		return s.writeCalendar(ctx, peer, args)
	default:
		return nil, fmt.Errorf("unknown operation %q", request.Op)
	}
}

func openPort(config connectionConfig) (datalink.Port, error) {
	if config.Network == 65535 {
		return nil, errors.New("link network must be 0..65534")
	}
	switch config.Transport {
	case "ip":
		local, err := netip.ParseAddrPort(config.Bind)
		if err != nil || !local.Addr().Is4() {
			return nil, errors.New("bind must be a numeric IPv4 address:port")
		}
		broadcast, err := netip.ParseAddrPort(config.Broadcast)
		if err != nil || !broadcast.Addr().Is4() || broadcast.Port() == 0 {
			return nil, errors.New("broadcast must be a numeric IPv4 address:port")
		}
		portConfig := bip.PortConfig{Local: local, Broadcast: broadcast, Network: types.NetworkNumber(config.Network)}
		// Windows delivers subnet broadcast replies to a wildcard listener.
		// Keep the selected interface as the advertised unicast identity.
		if runtime.GOOS == "windows" && !local.Addr().IsLoopback() && !local.Addr().IsUnspecified() && local.Port() != 0 {
			portConfig.Local = netip.AddrPortFrom(netip.IPv4Unspecified(), local.Port())
			portConfig.AdvertisedAddress = local.Addr()
		}
		if config.BBMD != "" {
			if config.TTL < 30 || config.TTL > 3600 {
				return nil, errors.New("foreign-device TTL must be 30..3600 seconds")
			}
			endpoint, err := netip.ParseAddrPort(config.BBMD)
			if err != nil {
				return nil, err
			}
			bbmd, err := bip.EndpointFromAddrPort(endpoint)
			if err != nil {
				return nil, err
			}
			foreign, err := bip.NewForeignDevice(bip.ForeignDeviceConfig{BBMD: bbmd, TTL: config.TTL})
			if err != nil {
				return nil, err
			}
			return bip.ListenForeignDevicePort(portConfig, foreign)
		}
		return bip.ListenNormalPort(portConfig)
	case "ipv6":
		return openIPv6(config)
	case "sc":
		return openSC(config)
	case "mstp":
		if !config.ConfirmJoin {
			return nil, errors.New("explicit confirmation of unused MAC and exclusive serial ownership is required")
		}
		if config.MAC < 0 || config.MAC > 127 || config.MaxMaster < config.MAC || config.MaxMaster > 127 || config.Network == 0 {
			return nil, errors.New("MS/TP requires MAC 0..127, Max_Master >= MAC and network 1..65534")
		}
		master, err := mstp.DefaultMasterConfig(byte(config.MAC), config.Baud)
		if err != nil {
			return nil, err
		}
		master.MaxMaster = byte(config.MaxMaster)
		wire, err := serialport.Open(serialport.Config{Name: config.Serial, BaudRate: config.Baud, ReadTimeout: time.Millisecond, DTR: true})
		if err != nil {
			return nil, err
		}
		port, err := mstp.NewPort(mstp.PortConfig{Network: types.NetworkNumber(config.Network), Master: master}, wire)
		if err != nil {
			_ = wire.Close()
		}
		return port, err
	default:
		return nil, errors.New("transport must be ip, mstp, ipv6 or sc")
	}
}

func (b *backend) connect(ctx context.Context, config connectionConfig) (any, error) {
	if b.session != nil {
		return nil, errors.New("disconnect the current link first")
	}
	if config.TimeoutMS < 100 || config.TimeoutMS > 30000 || config.Retries < 0 || config.Retries > 5 {
		return nil, errors.New("timeout must be 100..30000 ms and retries 0..5")
	}
	port, err := openPort(config)
	if err != nil {
		return nil, err
	}
	return b.connectPort(ctx, config, port)
}

func (b *backend) connectPort(ctx context.Context, config connectionConfig, port datalink.Port) (any, error) {
	s := &session{peers: make(map[string]peerInfo), done: make(chan struct{})}
	policy := transaction.DefaultClientPolicy()
	policy.APDUTimeout = time.Duration(config.TimeoutMS) * time.Millisecond
	policy.NumberOfAPDURetries = uint8(config.Retries)
	node, err := device.NewDataLinkNode(port, device.DataLinkNodeConfig{Application: transaction.ApplicationConfig{
		Policy: &policy, TransactionCapacity: 16,
		Observers: []transaction.Observer{transaction.ObserverFunc(s.observe)},
		Server:    device.ApplicationServerHandler{COVNotifications: device.COVNotificationReceiverFunc(s.receiveCOV)},
	}})
	if err != nil {
		_ = port.Close()
		return nil, err
	}
	s.node = node
	runContext, cancel := context.WithCancel(context.Background())
	s.cancel = cancel
	go func() {
		err := node.Run(runContext)
		s.mu.Lock()
		s.err = err
		s.mu.Unlock()
		close(s.done)
	}()
	startupTimeout := 20 * time.Second
	if config.Transport == "mstp" {
		startupTimeout = 60 * time.Second
	}
	ready, stop := context.WithTimeout(ctx, startupTimeout)
	defer stop()
	if err = node.WaitReady(ready); err == nil {
		ticker := time.NewTicker(time.Millisecond)
		defer ticker.Stop()
		for !node.Running() && err == nil {
			select {
			case <-ready.Done():
				err = ready.Err()
			case <-s.done:
				err = errors.New("BACnet node stopped during startup")
			case <-ticker.C:
			}
		}
	}
	if err == nil {
		if master, ok := port.(*mstp.Port); ok {
			err = master.WaitForParticipation(ready)
		}
	}
	if err != nil {
		cancel()
		_ = node.Close()
		<-s.done
		return nil, err
	}
	b.session = s
	address := config.Serial
	if ip, ok := port.(*bip.Port); ok {
		address = ip.LocalAddr().String()
	}
	return map[string]any{"address": address, "transport": config.Transport}, nil
}

func (b *backend) disconnect() error {
	s := b.session
	if s == nil {
		return nil
	}
	b.session = nil
	s.cancel()
	err := s.node.Close()
	<-s.done
	return err
}

func objectText(id types.ObjectIdentifier) string {
	return fmt.Sprintf("%d:%d", id.Type(), id.Instance())
}

func peerKey(id types.ObjectIdentifier, peer network.Peer) string {
	return fmt.Sprintf("%s/%d/%x/%d/%x", objectText(id), peer.Address().Network(), peer.Address().MAC(), peer.LinkNetwork(), peer.NextHop().MAC())
}

func (s *session) observe(_ context.Context, inbound transaction.Inbound) {
	message, err := service.ParseIAm(inbound.APDU(), encoding.Limits{})
	metadata := map[string]any{"at": time.Now().UTC(), "sourceNetwork": inbound.Network().Source().Address().Network(), "sourceMAC": fmt.Sprintf("%x", inbound.Network().Source().Address().MAC()), "type": fmt.Sprint(inbound.Message().Type()), "bytes": len(inbound.APDU())}
	if raw := inbound.APDU(); err != nil && len(raw) >= 2 && raw[0] == 0x10 && raw[1] == 0 {
		metadata["discoveryError"] = err.Error()
		metadata["iamHex"] = hex.EncodeToString(raw)
	}
	s.mu.Lock()
	if len(s.traffic) == 1000 {
		s.traffic = s.traffic[1:]
	}
	s.traffic = append(s.traffic, metadata)
	s.mu.Unlock()
	if err != nil {
		return
	}
	peer := inbound.Network().Source()
	key := peerKey(message.DeviceIdentifier, peer)
	address := fmt.Sprintf("%x", peer.NextHop().MAC())
	_, ipv4 := s.node.Port().(*bip.Port)
	if ipv4 && len(peer.NextHop().MAC()) == 6 {
		if endpoint, err := bip.ParseEndpoint(peer.NextHop().MAC()); err == nil {
			address = endpoint.AddrPort().String()
		}
	}
	info := peerInfo{Key: key, Device: objectText(message.DeviceIdentifier), Instance: uint32(message.DeviceIdentifier.Instance()), Address: address,
		Route: fmt.Sprintf("%d:%x", peer.Address().Network(), peer.Address().MAC()), Vendor: uint16(message.VendorID), MaxAPDU: int(message.MaxAPDU),
		Segmentation: fmt.Sprint(message.Segmentation), Seen: time.Now().UTC(), peer: peer}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, exists := s.peers[key]; !exists && len(s.peers) >= 1024 {
		return
	}
	info.Name = s.peers[key].Name
	s.peers[key] = info
}

func (s *session) devices() []peerInfo {
	routes := []network.Route{}
	if s.node != nil {
		routes = s.node.Network().Routes()
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	counts := make(map[uint32]int)
	for _, peer := range s.peers {
		counts[peer.Instance]++
	}
	result := make([]peerInfo, 0, len(s.peers))
	for _, peer := range s.peers {
		peer.RemoteNetwork = uint16(peer.peer.Address().Network())
		for _, route := range routes {
			if bytes.Equal(route.NextHop.MAC(), peer.peer.NextHop().MAC()) && peer.RemoteNetwork == 0 {
				peer.Router = true
				peer.Networks = append(peer.Networks, uint16(route.Network))
			}
		}
		sort.Slice(peer.Networks, func(i, j int) bool { return peer.Networks[i] < peer.Networks[j] })
		peer.Duplicate = counts[peer.Instance] > 1
		result = append(result, peer)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Key < result[j].Key })
	return result
}

func (s *session) lookup(key string) (peerInfo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	peer, ok := s.peers[key]
	if !ok {
		return peerInfo{}, errors.New("device binding is absent; discover again")
	}
	return peer, nil
}

type selection struct {
	Object   string  `json:"object"`
	Property string  `json:"property"`
	Index    *uint32 `json:"index,omitempty"`
}

type initialValue struct {
	Property  string  `json:"property"`
	Index     *uint32 `json:"index"`
	ValueType string  `json:"valueType"`
	Value     string  `json:"value"`
}

type operationArgs struct {
	Device                string              `json:"device"`
	Object                string              `json:"object"`
	Property              string              `json:"property"`
	Index                 *uint32             `json:"index"`
	Selections            []selection         `json:"selections"`
	ValueType             string              `json:"valueType"`
	Value                 string              `json:"value"`
	Priority              int                 `json:"priority"`
	Confirmed             bool                `json:"confirmed"`
	Target                string              `json:"target"`
	Seconds               int                 `json:"seconds"`
	Low                   *uint32             `json:"low"`
	High                  *uint32             `json:"high"`
	After                 uint64              `json:"after"`
	Process               uint32              `json:"process"`
	Lifetime              uint32              `json:"lifetime"`
	Start                 uint64              `json:"start"`
	Count                 int16               `json:"count"`
	RemoteNetwork         uint16              `json:"remoteNetwork"`
	Offset                int64               `json:"offset"`
	Data                  []byte              `json:"data"`
	Records               [][]byte            `json:"records"`
	RecordMode            bool                `json:"recordMode"`
	FileCount             uint64              `json:"fileCount"`
	EventState            uint32              `json:"eventState"`
	EventTime             service.TimeStamp   `json:"eventTime"`
	Source                string              `json:"source"`
	Password              string              `json:"password"`
	State                 uint32              `json:"state"`
	AllocateInstance      bool                `json:"allocateInstance"`
	ObjectType            uint16              `json:"objectType"`
	InitialValues         []initialValue      `json:"initialValues"`
	Minutes               *uint16             `json:"minutes"`
	ExpectedRaw           string              `json:"expectedRaw"`
	DefaultType           string              `json:"defaultType"`
	DefaultValue          string              `json:"defaultValue"`
	ExpectedDefaultRaw    string              `json:"expectedDefaultRaw"`
	Rules                 []calendarRule      `json:"rules"`
	ExpectedListRaw       *string             `json:"expectedListRaw"`
	Exceptions            []scheduleException `json:"exceptions"`
	ExpectedExceptionsRaw *string             `json:"expectedExceptionsRaw"`
	PeriodStart           string              `json:"periodStart"`
	PeriodEnd             string              `json:"periodEnd"`
	ExpectedPeriodRaw     *string             `json:"expectedPeriodRaw"`
}

func (s *session) discover(ctx context.Context, args operationArgs) (any, error) {
	if args.Seconds < 1 || args.Seconds > 120 {
		return nil, errors.New("discovery window must be 1..120 seconds")
	}
	nameContext, cancelNames := context.WithCancel(ctx)
	var names sync.WaitGroup
	jobs := make(chan peerInfo, 16)
	workers := 4
	if _, serial := s.node.Port().(*mstp.Port); serial {
		workers = 1
	}
	for range workers {
		names.Add(1)
		go func() {
			defer names.Done()
			for {
				select {
				case <-nameContext.Done():
					return
				case peer := <-jobs:
					s.resolveName(nameContext, peer)
				}
			}
		}()
	}
	defer func() { cancelNames(); names.Wait() }()
	queuedNames := make(map[string]bool)
	request := service.WhoIs{}
	if args.Low != nil || args.High != nil {
		if args.Low == nil || args.High == nil {
			return nil, errors.New("both device limits are required")
		}
		request = service.WhoIs{Limited: true, LowLimit: types.ObjectInstance(*args.Low), HighLimit: types.ObjectInstance(*args.High)}
	}
	payload, err := service.AppendWhoIs(nil, request)
	if err != nil {
		return nil, err
	}
	if args.Target == "" {
		if err := s.node.Network().SendWhoIsRouterToNetwork(ctx, s.node.LinkNetwork(), nil); err != nil {
			return nil, err
		}
	}
	if args.RemoteNetwork != 0 {
		if args.RemoteNetwork == 65535 && args.Target == "" {
			if err := s.node.SendLocalBroadcast(ctx, payload); err != nil {
				return nil, err
			}
		}
		if args.Target != "" {
			return nil, errors.New("choose a directed target or remote network, not both")
		}
		destination, addressErr := types.NewAddress(types.NetworkNumber(args.RemoteNetwork), nil)
		if addressErr != nil {
			return nil, addressErr
		}
		err = s.node.SendBroadcast(ctx, destination, payload)
	} else if args.Target == "" {
		err = s.node.SendLocalBroadcast(ctx, payload)
	} else {
		var mac []byte
		if _, ip := s.node.Port().(*bip.Port); ip {
			endpoint, parseErr := netip.ParseAddrPort(args.Target)
			if address, addressErr := netip.ParseAddr(args.Target); addressErr == nil && address.Is4() {
				endpoint = netip.AddrPortFrom(address, 47808)
				parseErr = nil
			}
			if parseErr != nil {
				return nil, parseErr
			}
			address, parseErr := bip.EndpointFromAddrPort(endpoint)
			if parseErr != nil {
				return nil, parseErr
			}
			mac, err = address.AppendTo(nil)
		} else {
			mac, err = hex.DecodeString(args.Target)
		}
		if err != nil {
			return nil, err
		}
		peer, peerErr := s.node.PeerForLocalMAC(mac)
		if peerErr != nil {
			return nil, peerErr
		}
		err = s.node.SendUnconfirmed(ctx, peer, payload)
	}
	if err != nil {
		return nil, err
	}
	timer := time.NewTimer(time.Duration(args.Seconds) * time.Second)
	defer timer.Stop()
	retry := time.NewTicker(2 * time.Second)
	defer retry.Stop()
	updates := time.NewTicker(250 * time.Millisecond)
	defer updates.Stop()
	for {
		select {
		case <-ctx.Done():
			return s.devices(), ctx.Err()
		case <-timer.C:
			return s.devices(), nil
		case <-updates.C:
			peers := s.devices()
			for _, peer := range peers {
				if peer.Name != "" || queuedNames[peer.Key] {
					continue
				}
				select {
				case jobs <- peer:
					queuedNames[peer.Key] = true
				default:
				}
			}
			reportProgress(ctx, peers)
		case <-retry.C:
			if args.Target == "" && (args.RemoteNetwork == 0 || args.RemoteNetwork == 65535) {
				if err := s.node.SendLocalBroadcast(ctx, payload); err != nil {
					return s.devices(), err
				}
			}
		}
	}
}

func (s *session) resolveName(ctx context.Context, peer peerInfo) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	id, err := types.ParseObjectIdentifier(peer.Device)
	if err != nil {
		return
	}
	ack, err := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyObjectName}, encoding.Limits{MaxValueLength: 4096})
	if err != nil {
		return
	}
	value, consumed, err := encoding.DecodeCharacterString(ack.Value, encoding.Limits{})
	if err != nil || consumed != len(ack.Value) {
		return
	}
	text, err := value.Text()
	if err != nil || len(text) > 1024 {
		return
	}
	s.mu.Lock()
	if current, exists := s.peers[peer.Key]; exists {
		current.Name = text
		s.peers[peer.Key] = current
	}
	s.mu.Unlock()
}

func parseSelection(item selection) (remote.Selection, error) {
	id, err := types.ParseObjectIdentifier(item.Object)
	if err != nil {
		return remote.Selection{}, err
	}
	property, err := types.ParsePropertyIdentifier(item.Property)
	if err != nil {
		return remote.Selection{}, err
	}
	index := service.ArrayIndex{}
	if item.Index != nil {
		index = service.ArrayIndex{Present: true, Value: *item.Index}
	}
	return remote.Selection{ObjectIdentifier: id, PropertyIdentifier: property, ArrayIndex: index}, nil
}

func makeRow(peer string, id types.ObjectIdentifier, result service.PropertyResult) propertyRow {
	row := propertyRow{Device: peer, Object: objectText(id), Property: result.PropertyIdentifier.String(), At: time.Now().UTC()}
	if result.ArrayIndex.Present {
		value := result.ArrayIndex.Value
		row.Index = &value
	}
	if result.Error != nil {
		row.Error = fmt.Sprintf("BACnet error class %d, code %d", result.Error.Class, result.Error.Code)
		return row
	}
	row.Raw = hex.EncodeToString(result.Value)
	row.Value = propertyvalue.Format(result.PropertyIdentifier, result.Value)
	row.Type = valueType(result.Value)
	row.EditorType, row.EditorValue = propertyEditorPrimitive(result.Value)
	if !result.ArrayIndex.Present {
		row.ArrayTypes, row.ArrayValues = propertyEditorArray(result.Value, result.PropertyIdentifier == types.PropertyStateText)
	}
	if result.PropertyIdentifier == types.PropertyPriorityArray && !result.ArrayIndex.Present {
		remaining := result.Value
		for len(remaining) > 0 && len(row.Elements) < 17 {
			_, consumed, err := encoding.DecodeApplicationPrimitive(remaining, encoding.Limits{})
			if err != nil || consumed <= 0 {
				row.Elements = nil
				break
			}
			row.Elements = append(row.Elements, propertyvalue.FormatPrimitive(remaining[:consumed]))
			remaining = remaining[consumed:]
		}
		if len(row.Elements) != 16 || len(remaining) != 0 {
			row.Elements = nil
		}
	}
	return row
}

func valueType(value []byte) string {
	header, _, err := encoding.ParseHeader(value, encoding.Limits{})
	if err != nil || header.Class != encoding.ApplicationClass {
		return "raw"
	}
	names := []string{"null", "boolean", "unsigned", "signed", "real", "double", "octet-string", "character-string", "bit-string", "enumerated", "date", "time", "object-identifier"}
	if int(header.Number) >= len(names) {
		return "raw"
	}
	return names[header.Number]
}

func (s *session) read(ctx context.Context, peer peerInfo, selections []selection) (any, error) {
	if len(selections) < 1 || len(selections) > 30000 {
		return nil, errors.New("select 1..30000 properties")
	}
	plan := make([]remote.Selection, 0, len(selections))
	for _, item := range selections {
		parsed, err := parseSelection(item)
		if err != nil {
			return nil, err
		}
		plan = append(plan, parsed)
	}
	maxAPDU := min(peer.MaxAPDU, 1476)
	rows := []propertyRow{}
	rpm, rp, splits, totalBytes := 0, 0, 0, 0
	for start := 0; start < len(plan); start += 16 {
		batchStart := len(rows)
		result, err := remote.ReadPropertiesDataLink(ctx, s.node, peer.peer, plan[start:min(start+16, len(plan))], remote.BatchReadConfig{MaxRequestAPDU: maxAPDU, MaxSelections: 16, Encoding: encoding.Limits{MaxInputLength: 1 << 20, MaxValueLength: 256 << 10}}, nil)
		rpm += result.ReadPropertyMultiple
		rp += result.ReadProperty
		splits += result.Splits
		for _, object := range result.Objects {
			for _, property := range object.Results {
				totalBytes += len(property.Value)
				if totalBytes > 4<<20 {
					return map[string]any{"rows": rows, "rpm": rpm, "rp": rp, "splits": splits}, errors.New("property results exceed 4 MiB; narrow the selection")
				}
				rows = append(rows, makeRow(peer.Key, object.ObjectIdentifier, property))
			}
		}
		reportProgress(ctx, map[string]any{"rows": rows[batchStart:], "rpm": rpm, "rp": rp, "splits": splits})
		if err != nil {
			return map[string]any{"rows": rows, "rpm": rpm, "rp": rp, "splits": splits}, err
		}
	}
	return map[string]any{"rows": rows, "rpm": rpm, "rp": rp, "splits": splits}, nil
}

func (s *session) inventory(ctx context.Context, peer peerInfo) (any, error) {
	id, err := types.ParseObjectIdentifier(peer.Device)
	if err != nil {
		return nil, err
	}
	result, err := s.node.ReadObjectList(ctx, peer.peer, id, device.ObjectListLimits{MaxObjects: 10000, MaxRequestAPDU: min(peer.MaxAPDU, 480)})
	if err != nil {
		return nil, err
	}
	objects := make([]map[string]string, 0, len(result.Objects))
	plan := make([]selection, 0, len(result.Objects)*3)
	for _, id := range result.Objects {
		text := objectText(id)
		objects = append(objects, map[string]string{"object": text, "kind": id.Type().String()})
		for _, property := range []string{"object-name", "present-value", "units"} {
			plan = append(plan, selection{Object: text, Property: property})
		}
	}
	enrichment, readErr := s.read(ctx, peer, plan)
	response := map[string]any{"objects": objects, "enrichment": enrichment, "indexedFallback": result.IndexedFallback, "stats": result.Stats}
	if readErr != nil {
		response["enrichmentError"] = readErr.Error()
	}
	return response, nil
}

// Page through the shared Object_List reader so large devices become
// browsable without waiting for per-object property enrichment.
func (s *session) objectList(ctx context.Context, peer peerInfo) (any, error) {
	id, err := types.ParseObjectIdentifier(peer.Device)
	if err != nil {
		return nil, err
	}
	objects := []map[string]string{}
	stats := device.ObjectListStats{}
	for offset := 0; ; {
		page, err := s.node.ReadObjectListPage(ctx, peer.peer, id, device.ObjectListPageOptions{
			Offset: offset, Limit: 64,
			Limits: device.ObjectListLimits{MaxObjects: 10000, MaxRequestAPDU: min(peer.MaxAPDU, 1476)},
		}, nil)
		if err != nil {
			return nil, err
		}
		chunk := make([]map[string]string, 0, len(page.Objects))
		for _, object := range page.Objects {
			chunk = append(chunk, map[string]string{"object": objectText(object), "kind": object.Type().String()})
		}
		objects = append(objects, chunk...)
		stats.ReadProperty += page.Stats.ReadProperty
		stats.ReadPropertyMultiple += page.Stats.ReadPropertyMultiple
		stats.Splits += page.Stats.Splits
		reportProgress(ctx, map[string]any{"objects": chunk, "completed": len(objects), "total": page.Total})
		if !page.HasMore {
			return map[string]any{"objects": objects, "indexedFallback": true, "stats": stats}, nil
		}
		if len(page.Objects) == 0 {
			return nil, errors.New("Object_List page made no progress")
		}
		offset += len(page.Objects)
	}
}

func (s *session) properties(ctx context.Context, peer peerInfo, object string) (any, error) {
	id, err := types.ParseObjectIdentifier(object)
	if err != nil {
		return nil, err
	}
	ack, err := s.node.ReadPropertyMultiple(ctx, peer.peer, service.ReadPropertyMultipleRequest{Specifications: []service.ReadAccessSpecification{{ObjectIdentifier: id, Properties: []service.PropertyReference{{PropertyIdentifier: types.PropertyAll}}}}}, service.MultipleLimits{})
	if err == nil {
		rows := []propertyRow{}
		for _, obj := range ack.Objects {
			for _, property := range obj.Results {
				if property.PropertyIdentifier == types.PropertyAll && property.Error != nil {
					err = fmt.Errorf("RPM ALL rejected: BACnet error class %d, code %d", property.Error.Class, property.Error.Code)
				}
				rows = append(rows, makeRow(peer.Key, obj.ObjectIdentifier, property))
			}
		}
		if err == nil && len(rows) != 0 {
			return map[string]any{"rows": rows, "mode": "ReadPropertyMultiple ALL"}, nil
		}
		if err == nil {
			err = errors.New("RPM ALL returned no properties")
		}
	}
	// Recover the peer's advertised properties when an aggregate ALL exchange
	// cannot be served. Legacy peers without Property_List retain explicit reads.
	plan := []selection{}
	listed, listErr := s.node.ReadProperty(ctx, peer.peer, service.ReadPropertyRequest{ObjectIdentifier: id, PropertyIdentifier: types.PropertyPropertyList}, encoding.Limits{})
	if listErr == nil {
		properties := []types.PropertyIdentifier{types.PropertyObjectIdentifier, types.PropertyObjectName, types.PropertyObjectType, types.PropertyPropertyList}
		seen := make(map[types.PropertyIdentifier]bool)
		remaining := listed.Value
		for len(remaining) != 0 && len(properties) < 2048 {
			property, used, decodeErr := encoding.DecodeEnumerated(remaining, encoding.Limits{})
			if decodeErr != nil || property > 4194303 {
				listErr = errors.New("invalid Property_List response")
				break
			}
			properties = append(properties, types.PropertyIdentifier(property))
			remaining = remaining[used:]
		}
		if len(remaining) != 0 {
			listErr = errors.New("Property_List exceeds the desktop property limit or contains malformed values")
		}
		if listErr == nil {
			for _, property := range properties {
				if !seen[property] {
					seen[property] = true
					item := selection{Object: object, Property: fmt.Sprint(uint32(property))}
					if property == types.PropertyObjectList {
						index := uint32(0)
						item.Index = &index
					}
					plan = append(plan, item)
				}
			}
		}
	}
	if listErr != nil {
		for _, property := range []string{"object-identifier", "object-name", "object-type", "description", "present-value", "status-flags", "event-state", "out-of-service", "units", "priority-array", "relinquish-default", "property-list"} {
			plan = append(plan, selection{Object: object, Property: property})
		}
	}
	result, readErr := s.read(ctx, peer, plan)
	if readErr != nil && device.IsReadPropertyMultipleCompatibilityError(readErr) {
		rows := []propertyRow{}
		if result != nil {
			rows = result.(map[string]any)["rows"].([]propertyRow)
		}
		for _, item := range plan {
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			parsed, _ := parseSelection(item)
			found := false
			for _, row := range rows {
				if row.Property == parsed.PropertyIdentifier.String() && (row.Index == nil && item.Index == nil || row.Index != nil && item.Index != nil && *row.Index == *item.Index) {
					found = true
					break
				}
			}
			if found {
				continue
			}
			one, oneErr := s.read(ctx, peer, []selection{item})
			if oneErr == nil {
				rows = append(rows, one.(map[string]any)["rows"].([]propertyRow)...)
				continue
			}
			if !device.IsReadPropertyMultipleCompatibilityError(oneErr) {
				return nil, oneErr
			}
			rows = append(rows, propertyRow{Device: peer.Key, Object: object, Property: parsed.PropertyIdentifier.String(), Index: item.Index, Error: oneErr.Error(), At: time.Now().UTC()})
		}
		result = map[string]any{"rows": rows, "mode": "Explicit property reads", "readWarning": readErr.Error()}
		readErr = nil
	}
	if result != nil {
		result.(map[string]any)["fallbackReason"] = err.Error()
	}
	return result, readErr
}

func (s *session) write(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if !args.Confirmed {
		return nil, errors.New("write requires explicit operator confirmation")
	}
	if args.Priority < 0 || args.Priority > 16 {
		return nil, errors.New("priority must be 0 (omitted) or 1..16")
	}
	parsed, err := parseSelection(selection{Object: args.Object, Property: args.Property, Index: args.Index})
	if err != nil {
		return nil, err
	}
	value, err := s.encodeEditorWrite(ctx, peer, args)
	if err != nil {
		return nil, err
	}
	if len(value) > 65536 {
		return nil, errors.New("write value exceeds 64 KiB")
	}
	readbackID := parsed.ObjectIdentifier
	if parsed.PropertyIdentifier == types.PropertyObjectIdentifier {
		changed, consumed, decodeErr := encoding.DecodeObjectIdentifier(value, encoding.Limits{})
		if decodeErr != nil || consumed != len(value) || changed.Type() != parsed.ObjectIdentifier.Type() || changed.Instance() == types.MaxObjectInstance {
			return nil, errors.New("enter a valid instance number without changing the object type")
		}
		readbackID = changed
	}
	request := service.WritePropertyRequest{ObjectIdentifier: parsed.ObjectIdentifier, PropertyIdentifier: parsed.PropertyIdentifier, ArrayIndex: parsed.ArrayIndex, Value: value, Priority: service.WritePriority{Present: args.Priority != 0, Value: uint64(args.Priority)}}
	err = s.node.WritePropertyWithCapabilities(ctx, peer.peer, transaction.PeerCapabilities{MaxAPDULengthAccepted: peer.MaxAPDU, CanReceiveSegmentation: peer.Segmentation == fmt.Sprint(service.SegmentationBoth) || peer.Segmentation == fmt.Sprint(service.SegmentationReceive)}, request, encoding.Limits{})
	if err != nil {
		return nil, err
	}
	// Rebind only after the remote ACK. Read back the new identifier rather
	// than requesting the object under the identifier that was just changed.
	var identity any
	if readbackID != parsed.ObjectIdentifier {
		oldKey := peer.Key
		if parsed.ObjectIdentifier.Type() == types.ObjectTypeDevice {
			peer.Device = objectText(readbackID)
			peer.Instance = uint32(readbackID.Instance())
			peer.Key = peerKey(readbackID, peer.peer)
			s.mu.Lock()
			delete(s.peers, oldKey)
			s.peers[peer.Key] = peer
			s.mu.Unlock()
		}
		identity = map[string]any{"oldKey": oldKey, "peer": peer, "oldObject": args.Object, "newObject": objectText(readbackID)}
	}
	result, readErr := s.read(ctx, peer, []selection{{Object: objectText(readbackID), Property: args.Property, Index: args.Index}})
	response := map[string]any{"acknowledged": true, "readback": result}
	if identity != nil {
		response["identity"] = identity
	}
	if parsed.PropertyIdentifier == types.PropertyObjectName && parsed.ObjectIdentifier.Type() == types.ObjectTypeDevice {
		if rows, ok := result.(map[string]any); ok {
			for _, row := range rows["rows"].([]propertyRow) {
				if row.Error == "" && row.EditorType == "character-string" {
					peer.Name = row.EditorValue
					s.mu.Lock()
					s.peers[peer.Key] = peer
					s.mu.Unlock()
				}
			}
		}
	}
	if readErr != nil {
		response["readbackError"] = readErr.Error()
	}
	return response, nil
}

func (s *session) subscribe(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if args.Process == 0 || args.Lifetime > 3600 {
		return nil, errors.New("COV requires a nonzero process ID and lifetime 0..3600 seconds")
	}
	id, err := types.ParseObjectIdentifier(args.Object)
	if err != nil {
		return nil, err
	}
	request := service.SubscribeCOVRequest{SubscriberProcessIdentifier: args.Process, MonitoredObjectIdentifier: id}
	if args.Lifetime > 0 {
		confirmed := false
		request.IssueConfirmedNotifications = &confirmed
		request.Lifetime = &args.Lifetime
	}
	return true, s.node.SubscribeCOV(ctx, peer.peer, request)
}

func (s *session) receiveCOV(_ context.Context, incoming device.IPServerRequest, message service.COVNotification, _ bool) {
	bytes := 0
	for _, value := range message.Values {
		bytes += len(value.Value)
	}
	if bytes > 65536 {
		s.mu.Lock()
		s.sequence++
		s.dropped++
		s.mu.Unlock()
		return
	}
	rows := make([]propertyRow, 0, len(message.Values))
	// The server adapter retains the original transport-neutral request.
	peer := ""
	if incoming.NetworkPeer != nil {
		peer = peerKey(message.InitiatingDeviceIdentifier, *incoming.NetworkPeer)
	}
	for _, property := range message.Values {
		rows = append(rows, makeRow(peer, message.MonitoredObjectIdentifier, service.PropertyResult{PropertyIdentifier: property.PropertyIdentifier, ArrayIndex: property.ArrayIndex, ValuePresent: true, Value: property.Value}))
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sequence++
	if len(s.events) == 256 {
		s.events = s.events[1:]
		s.dropped++
	}
	s.events = append(s.events, notification{Sequence: s.sequence, Peer: peer, Process: message.SubscriberProcessIdentifier, Rows: rows})
}

func (s *session) eventInformation(ctx context.Context, peer peerInfo) (any, error) {
	request := service.GetEventInformationRequest{}
	rows := []map[string]any{}
	for page := 0; page < 64; page++ {
		ack, err := s.node.GetEventInformation(ctx, peer.peer, request, service.EventInformationLimits{MaxSummaries: 1024})
		if err != nil {
			return nil, err
		}
		for _, event := range ack.Summaries {
			rows = append(rows, map[string]any{"object": objectText(event.ObjectIdentifier), "state": fmt.Sprint(event.EventState), "notifyType": fmt.Sprint(event.NotifyType), "acknowledged": fmt.Sprint(event.AcknowledgedTransitions), "priorities": event.EventPriorities, "timestamps": event.EventTimeStamps})
		}
		if !ack.MoreEvents {
			return rows, nil
		}
		if len(ack.Summaries) == 0 {
			return nil, errors.New("event page made no progress")
		}
		last := ack.Summaries[len(ack.Summaries)-1].ObjectIdentifier
		if request.LastReceivedObjectIdentifier != nil && last.Uint32() <= request.LastReceivedObjectIdentifier.Uint32() {
			return nil, errors.New("event cursor did not advance")
		}
		request.LastReceivedObjectIdentifier = &last
	}
	return nil, errors.New("event scan exceeds 64 pages")
}

func (s *session) readRange(ctx context.Context, peer peerInfo, args operationArgs) (any, error) {
	if args.Count < 1 || args.Count > 256 || args.Start < 1 {
		return nil, errors.New("range requires start >= 1 and count 1..256")
	}
	parsed, err := parseSelection(selection{Object: args.Object, Property: args.Property, Index: args.Index})
	if err != nil {
		return nil, err
	}
	ack, err := s.node.ReadRange(ctx, peer.peer, service.ReadRangeRequest{ObjectIdentifier: parsed.ObjectIdentifier, PropertyIdentifier: parsed.PropertyIdentifier, ArrayIndex: parsed.ArrayIndex, Range: service.ReadRangeSelection{Kind: service.ReadRangeByPosition, Reference: args.Start, Count: args.Count}})
	if err != nil {
		return nil, err
	}
	return map[string]any{"count": ack.ItemCount, "flags": ack.Flags, "firstSequence": ack.FirstSequenceNumber, "raw": hex.EncodeToString(ack.ItemData), "value": propertyvalue.Format(parsed.PropertyIdentifier, ack.ItemData)}, nil
}
