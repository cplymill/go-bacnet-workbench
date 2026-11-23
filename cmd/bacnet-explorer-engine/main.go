// Command bacnet-explorer-engine is the private, stdio-only desktop adapter.
// Protocol behavior belongs to the existing stack packages.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"sync"
	"time"
)

type rpcRequest struct {
	ID   int64           `json:"id"`
	Op   string          `json:"op"`
	Args json.RawMessage `json:"args"`
}

type rpcResponse struct {
	ID       int64             `json:"id"`
	Result   any               `json:"result,omitempty"`
	Error    string            `json:"error,omitempty"`
	Progress bool              `json:"progress,omitempty"`
	Failure  *operationFailure `json:"failure,omitempty"`
}

type progressKey struct{}

func reportProgress(ctx context.Context, result any) {
	if send, ok := ctx.Value(progressKey{}).(func(any)); ok {
		send(result)
	}
}

func main() {
	if err := serve(context.Background(), os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

// There is one admitted operation at a time. Cancellation is handled directly
// by the reader, so a slow peer never prevents Cancel or process shutdown.
func serve(parent context.Context, input io.Reader, output io.Writer) error {
	ctx, stop := context.WithCancel(parent)
	defer stop()
	b := &backend{}
	defer b.disconnect()
	var writer sync.Mutex
	var state sync.Mutex
	var activeID int64
	var cancel context.CancelFunc
	var workers sync.WaitGroup
	write := func(response rpcResponse) {
		encoded, err := json.Marshal(response)
		if err != nil || len(encoded) > 32<<20 {
			encoded, _ = json.Marshal(rpcResponse{ID: response.ID, Error: "result exceeds the desktop response limit; narrow the selection"})
		}
		writer.Lock()
		defer writer.Unlock()
		_, _ = output.Write(append(encoded, '\n'))
	}
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 4096), 1<<20)
	for scanner.Scan() {
		var request rpcRequest
		if err := decodeStrict(scanner.Bytes(), &request); err != nil || request.ID <= 0 {
			write(rpcResponse{ID: request.ID, Error: "invalid request envelope"})
			continue
		}
		if request.Op == "cancel" {
			state.Lock()
			if cancel != nil {
				cancel()
			}
			state.Unlock()
			write(rpcResponse{ID: request.ID, Result: true})
			continue
		}
		state.Lock()
		if activeID != 0 {
			state.Unlock()
			write(rpcResponse{ID: request.ID, Error: "another operation is active"})
			continue
		}
		operationContext, cancelOperation := context.WithTimeout(ctx, 3*time.Minute)
		operationContext = context.WithValue(operationContext, progressKey{}, func(result any) {
			write(rpcResponse{ID: request.ID, Result: result, Progress: true})
		})
		activeID, cancel = request.ID, cancelOperation
		state.Unlock()
		workers.Add(1)
		go func() {
			defer workers.Done()
			defer cancelOperation()
			result, err := b.dispatch(operationContext, request)
			response := rpcResponse{ID: request.ID, Result: result}
			if err != nil {
				response.Error = err.Error()
				response.Failure = explainFailure(request.Op, err)
			}
			state.Lock()
			activeID, cancel = 0, nil
			state.Unlock()
			write(response)
		}()
	}
	stop()
	workers.Wait()
	return scanner.Err()
}

func decodeStrict(data []byte, result any) error {
	decoder := json.NewDecoder(bytesReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(result); err != nil {
		return err
	}
	var extra any
	if err := decoder.Decode(&extra); !errors.Is(err, io.EOF) {
		return errors.New("trailing JSON")
	}
	return nil
}
