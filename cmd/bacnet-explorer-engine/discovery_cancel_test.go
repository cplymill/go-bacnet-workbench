package main

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"
)

func TestDirectedDiscoveryAcceptsBareIPv4AndCancels(t *testing.T) {
	// Loopback only: no query is sent to the user's reported address or lab.
	b := &backend{}
	if _, err := b.connect(context.Background(), connectionConfig{
		Transport: "ip", Bind: "127.0.0.1:0", Broadcast: "127.0.0.1:47808",
		TimeoutMS: 500, Retries: 0,
	}); err != nil {
		t.Fatal(err)
	}
	defer b.disconnect()
	for _, target := range []string{"127.0.0.1", "127.0.0.1:47808"} {
		t.Run(target, func(t *testing.T) {
			ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
			defer cancel()
			start := time.Now()
			_, err := b.session.discover(ctx, operationArgs{Target: target, Seconds: 120})
			if !errors.Is(err, context.DeadlineExceeded) {
				t.Fatalf("directed discovery did not honor cancellation: %v", err)
			}
			if time.Since(start) > time.Second {
				t.Fatal("cancellation waited for the discovery window")
			}
		})
	}
	for _, target := range []string{"127.0.0.1:abc", "127.0.0.999", "example.invalid"} {
		_, err := b.session.discover(context.Background(), operationArgs{Target: target, Seconds: 1})
		if err == nil || strings.Contains(err.Error(), "context") {
			t.Fatalf("invalid target %q was not rejected immediately: %v", target, err)
		}
	}
}
