package main

import (
	"context"
	"crypto/tls"
	"encoding/hex"
	"errors"
	"fmt"
	"net/netip"
	"os"
	"time"

	"github.com/cplymill/go-bacnet/datalink"
	"github.com/cplymill/go-bacnet/datalink/bip6"
	"github.com/cplymill/go-bacnet/datalink/sc"
	"github.com/cplymill/go-bacnet/types"
)

func openIPv6(config connectionConfig) (datalink.Port, error) {
	local, err := netip.ParseAddrPort(config.IPv6Bind)
	if err != nil || !local.Addr().Is6() || local.Addr().IsUnspecified() {
		return nil, errors.New("IPv6 bind must be an explicit [IPv6%zone]:port endpoint")
	}
	mac, err := hex.DecodeString(config.VMAC)
	if err != nil || len(mac) != 3 {
		return nil, errors.New("IPv6 VMAC must be exactly six hexadecimal digits")
	}
	vmac, err := bip6.ParseVMAC(mac)
	if err != nil {
		return nil, err
	}
	if config.InterfaceIndex < 1 {
		return nil, errors.New("IPv6 requires an explicit multicast interface index")
	}
	return bip6.ListenNormalPort(bip6.PortConfig{Network: types.NetworkNumber(config.Network), Local: local, VMAC: vmac, MulticastGroup: netip.MustParseAddr("ff02::bac0"), MulticastUDPPort: 47808, MulticastInterfaceIndex: config.InterfaceIndex})
}

func openSC(config connectionConfig) (datalink.Port, error) {
	mac, err := hex.DecodeString(config.VMAC)
	if err != nil || len(mac) != 6 {
		return nil, errors.New("SC VMAC must be exactly twelve hexadecimal digits")
	}
	vmac, err := sc.NewNodeVMAC([6]byte(mac))
	if err != nil {
		return nil, err
	}
	uuidBytes, err := hex.DecodeString(config.UUID)
	if err != nil || len(uuidBytes) != 16 {
		return nil, errors.New("SC UUID must be exactly 32 hexadecimal digits")
	}
	for _, path := range []string{config.Certificate, config.PrivateKey, config.SiteCA} {
		info, err := os.Stat(path)
		if err != nil {
			return nil, fmt.Errorf("SC credential file: %w", err)
		}
		if !info.Mode().IsRegular() || info.Size() > 1<<20 {
			return nil, errors.New("SC credential inputs must be regular files no larger than 1 MiB")
		}
	}
	credentials, err := loadSCClientTLS(config.Certificate, config.PrivateKey, config.SiteCA, config.VerifyServerName)
	if err != nil {
		return nil, err
	}
	identity := sc.ConnectionPayload{VMAC: vmac, DeviceUUID: [16]byte(uuidBytes), MaximumBVLCLength: 1600, MaximumNPDULength: 1497}
	nodeSwitch, err := sc.NewNodeSwitch(identity)
	if err != nil {
		return nil, err
	}
	hub, err := sc.StartHubConnector(context.Background(), sc.HubConnectorRuntimeConfig{Connector: sc.HubConnectorConfig{PrimaryURI: config.Hub, FailoverURI: config.FailoverHub}, Client: sc.HubClientConfig{Identity: identity, TLS: credentials, DisconnectWait: time.Second}})
	if err != nil {
		return nil, err
	}
	closeHub := func() {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		_ = hub.Close(ctx)
	}
	runtime, err := sc.StartNodeRuntime(context.Background(), sc.NodeRuntimeConfig{NodeSwitch: nodeSwitch, Hub: hub, DisconnectWait: time.Second})
	if err != nil {
		closeHub()
		return nil, err
	}
	port, err := sc.NewNodePort(sc.NodePortConfig{Runtime: runtime, Network: types.NetworkNumber(config.Network), LocalVMAC: vmac, MaxNPDU: 1497, CloseTimeout: time.Second})
	if err != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		_ = runtime.Close(ctx)
	}
	return port, err
}

// loadSCClientTLS adapts application-selected files to the public stack API.
// Issuer validation and TLS policy remain owned by datalink/sc.
func loadSCClientTLS(certificatePath, keyPath, issuerPath string, verifyName bool) (sc.ClientTLSOptions, error) {
	certificatePEM, err := os.ReadFile(certificatePath)
	if err != nil {
		return sc.ClientTLSOptions{}, fmt.Errorf("read SC certificate: %w", err)
	}
	keyPEM, err := os.ReadFile(keyPath)
	if err != nil {
		return sc.ClientTLSOptions{}, fmt.Errorf("read SC private key: %w", err)
	}
	defer clear(keyPEM)
	certificate, err := tls.X509KeyPair(certificatePEM, keyPEM)
	if err != nil {
		return sc.ClientTLSOptions{}, fmt.Errorf("load SC credential pair: %w", err)
	}
	issuerPEM, err := os.ReadFile(issuerPath)
	if err != nil {
		return sc.ClientTLSOptions{}, fmt.Errorf("read SC site CA: %w", err)
	}
	issuers, err := sc.NewIssuerStore(issuerPEM)
	if err != nil {
		return sc.ClientTLSOptions{}, err
	}
	return sc.ClientTLSOptions{Config: &tls.Config{Certificates: []tls.Certificate{certificate}}, Issuers: issuers, VerifyServerName: verifyName}, nil
}
