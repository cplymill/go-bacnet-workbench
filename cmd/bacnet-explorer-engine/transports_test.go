package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"math/big"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestSCApplicationCredentialBoundary(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "Loopback application fixture"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature, IsCA: true, BasicConstraintsValid: true}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	directory := t.TempDir()
	certificate := filepath.Join(directory, "certificate.pem")
	privateKey := filepath.Join(directory, "key.pem")
	issuer := filepath.Join(directory, "issuer.pem")
	for path, data := range map[string][]byte{certificate: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), privateKey: pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER}), issuer: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})} {
		if err := os.WriteFile(path, data, 0600); err != nil {
			t.Fatal(err)
		}
	}
	options, err := loadSCClientTLS(certificate, privateKey, issuer, true)
	if err != nil {
		t.Fatal(err)
	}
	if options.Config == nil || len(options.Config.Certificates) != 1 || options.Issuers == nil || !options.VerifyServerName || options.Config.InsecureSkipVerify {
		t.Fatal("public credential options lost authentication policy")
	}
	if _, err := loadSCClientTLS(certificate, privateKey, issuer+"-missing", true); err == nil {
		t.Fatal("accepted missing issuer file")
	}
	if err := os.WriteFile(issuer, []byte("invalid issuer"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := loadSCClientTLS(certificate, privateKey, issuer, false); err == nil {
		t.Fatal("accepted invalid issuer")
	}
	if err := os.WriteFile(privateKey, []byte("invalid private key"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := loadSCClientTLS(certificate, privateKey, issuer, true); err == nil {
		t.Fatal("accepted invalid private key")
	}
}
