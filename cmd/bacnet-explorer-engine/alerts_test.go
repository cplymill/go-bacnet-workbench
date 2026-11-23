package main

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/cplymill/go-bacnet/apdu"
	"github.com/cplymill/go-bacnet/device"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/transaction"
)

// App wording reuses typed shared-stack outcomes; these tests do not change
// service execution or protocol conformance. Reject is Clause 18.8.
func TestPlainLanguageFailures(t *testing.T) {
	reject := func(reason uint8) error {
		return &device.RemoteResponseError{PDUType: apdu.PDUReject, Reason: reason, InvokeID: 115}
	}
	abort := func(reason uint8) error {
		return &device.RemoteResponseError{PDUType: apdu.PDUAbort, Reason: reason, Server: true, InvokeID: 115}
	}
	protocol := func(code service.ErrorCode) error {
		return &device.RemoteResponseError{PDUType: apdu.PDUError, ProtocolError: &service.ProtocolError{Class: service.ErrorClassProperty, Code: code}}
	}
	cases := []struct {
		name, operation string
		err             error
		want, action    string
	}{
		{"range reject9", "range", reject(9), "ReadRange", "Read properties"},
		{"cov reject9", "subscribe", reject(9), "COV", "polling"},
		{"events reject9", "events", reject(9), "alarm", "Event State"},
		{"unknown reject", "read", reject(200), "cannot explain", "technical"},
		{"abort9 is resources", "read", abort(9), "resources", "fewer"},
		{"no segmentation", "read", abort(4), "split into parts", "smaller"},
		{"unknown property", "read", protocol(service.ErrorCodeUnknownProperty), "does not provide", "available properties"},
		{"write denied", "write", protocol(service.ErrorCodeWriteAccessDenied), "does not allow", "Access guidance"},
		{"invalid value", "write", protocol(service.ErrorCodeValueOutOfRange), "allowed range", "limits"},
		{"not cov", "subscribe", protocol(service.ErrorCodeNotCOVProperty), "change notifications", "polling"},
		{"read timeout", "read", transaction.ErrAPDUTimeout, "timed out", "online"},
		{"write timeout", "write", context.DeadlineExceeded, "timed out", "uncertain"},
		{"write abort", "schedule-write", abort(9), "resources", "read back"},
		{"cancel write", "write", context.Canceled, "canceled", "already have taken effect"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			wrapped := fmt.Errorf("operation context: %w", tc.err)
			result := explainFailure(tc.operation, wrapped)
			if result == nil || !strings.Contains(result.Message, tc.want) || !strings.Contains(result.Action, tc.action) {
				t.Fatalf("explanation: %+v", result)
			}
			if tc.operation == "read" && strings.Contains(result.Action, "change may") {
				t.Fatalf("read was described as a write: %+v", result)
			}
			if !errors.Is(wrapped, tc.err) {
				t.Fatal("typed protocol error lost")
			}
		})
	}
	if explainFailure("connect", errors.New("MAC must be 0..127")) != nil {
		t.Fatal("useful validation was replaced")
	}
}
