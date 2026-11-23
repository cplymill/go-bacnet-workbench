module github.com/cplymill/go-bacnet-workbench

go 1.25.13

toolchain go1.26.11

require (
	github.com/cplymill/go-bacnet v0.2.0-rc.5
	go.bug.st/serial v1.7.1
)

require (
	github.com/coder/websocket v1.8.15 // indirect
	golang.org/x/sys v0.47.0 // indirect
)

replace github.com/cplymill/go-bacnet => "../GO BACcnet Stack"
