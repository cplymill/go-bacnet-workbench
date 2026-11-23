package main

import (
	"context"
	"errors"
	"net"

	"github.com/cplymill/go-bacnet/apdu"
	"github.com/cplymill/go-bacnet/device"
	"github.com/cplymill/go-bacnet/service"
	"github.com/cplymill/go-bacnet/transaction"
)

// This is application presentation only. The shared stack still owns the
// protocol result, and the original error remains in the RPC envelope.
type operationFailure struct {
	Message string `json:"message"`
	Action  string `json:"action"`
}

func explainFailure(operation string, err error) *operationFailure {
	failure := &operationFailure{}
	var remote *device.RemoteResponseError
	if errors.As(err, &remote) {
		switch remote.PDUType {
		case apdu.PDUReject:
			switch remote.Reason {
			case apdu.RejectUnrecognizedService:
				switch operation {
				case "range":
					failure.Message = "This device does not recognize requests to read a range of list or log entries (ReadRange)."
					failure.Action = "Use Read properties for point values. Reading stored history requires a device that supports ReadRange."
				case "subscribe":
					failure.Message = "This device does not recognize change-notification subscription requests (COV)."
					failure.Action = "Use polling in Watch & trends to monitor values."
				case "events":
					failure.Message = "This device does not recognize requests for active alarm and event information."
					failure.Action = "Check the device's supported alarm services. Event State on individual objects may still be readable."
				default:
					failure.Message = "This device does not recognize the requested BACnet operation."
					failure.Action = "Check the device's supported services or use another supported operation."
				}
			case apdu.RejectBufferOverflow, apdu.RejectTooManyArguments:
				failure.Message = "The device rejected the request because it was too large or contained too many items."
				failure.Action = "Request fewer objects, properties or list entries at once."
			case apdu.RejectInconsistentParameters, apdu.RejectInvalidParameterDataType, apdu.RejectInvalidTag, apdu.RejectMissingRequiredParameter, apdu.RejectParameterOutOfRange, apdu.RejectUndefinedEnumeration, apdu.RejectInvalidDataEncoding:
				failure.Message = "The device could not understand one or more request parameters."
				failure.Action = "Check the selected object, property, array index and value type. If they are correct, copy the technical details for troubleshooting."
			default:
				failure.Message = "The device rejected the request for a reason Workbench cannot explain."
				failure.Action = "Copy the technical details and check the device documentation."
			}
		case apdu.PDUAbort:
			switch remote.Reason {
			case apdu.AbortBufferOverflow, apdu.AbortAPDUTooLong:
				failure.Message = "The operation stopped because the request or response was too large."
				failure.Action = "Request fewer objects, properties or list entries at once."
			case apdu.AbortSegmentationNotSupported:
				failure.Message = "The operation needs a message split into parts, which this exchange could not support."
				failure.Action = "Try a smaller request or read individual properties."
			case apdu.AbortOutOfResources:
				failure.Message = "The operation stopped because the device or client ran out of resources."
				failure.Action = "Reduce polling or request fewer items before trying another read."
			case apdu.AbortApplicationExceededReplyTime, apdu.AbortTSMTimeout:
				failure.Message = "The operation stopped because a response did not arrive in time."
				failure.Action = "Check the device and network, and try a smaller read request."
			case apdu.AbortSecurityError, apdu.AbortInsufficientSecurity:
				failure.Message = "The operation stopped because its security requirements were not met."
				failure.Action = "Check the device's access settings and the selected connection credentials."
			default:
				failure.Message = "The device or client stopped the BACnet operation before it completed."
				failure.Action = "Copy the technical details and check the device and connection."
			}
			if changesDevice(operation) {
				failure.Action += " The result of a sent change may be uncertain; read back before sending it again."
			}
		case apdu.PDUError:
			if remote.ProtocolError == nil {
				failure.Message = "The device reported that it could not complete this operation."
				failure.Action = "Copy the technical details and check the device documentation."
				break
			}
			return explainProtocolFailure(*remote.ProtocolError)
		}
	} else if errors.Is(err, context.Canceled) {
		failure.Message = "The operation was canceled."
		failure.Action = "Start the operation again when you are ready."
		if changesDevice(operation) {
			failure.Action = "A sent change may already have taken effect. Read back before sending it again."
		}
	} else if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, transaction.ErrAPDUTimeout) || errors.Is(err, transaction.ErrAPDUSegmentTimeout) {
		failure.Message = "The device did not reply before the operation timed out."
		if errors.Is(err, context.DeadlineExceeded) {
			failure.Message = "The operation timed out before it could finish."
		}
		failure.Action = "Check that the device is online and the network or serial settings are correct. Try a smaller read request."
		if changesDevice(operation) {
			failure.Action = "Check the connection, then read back the device before sending the change again; the result may be uncertain."
		}
	} else {
		var networkError *net.OpError
		if errors.As(err, &networkError) {
			failure.Message = "Workbench could not communicate on the selected network connection."
			failure.Action = "Check the selected adapter, address and port, and whether another application is using them."
			if changesDevice(operation) {
				failure.Action += " Read back any sent change before retrying it."
			}
		}
	}
	if failure.Message == "" {
		return nil
	}
	return failure
}

func explainProtocolFailure(problem service.ProtocolError) *operationFailure {
	failure := &operationFailure{}
	switch problem.Code {
	case service.ErrorCodeUnknownObject:
		failure.Message = "The device could not find the selected object."
		failure.Action = "Refresh the device's object list and select the object again."
	case service.ErrorCodeUnknownProperty:
		failure.Message = "The device does not provide the selected property on this object."
		failure.Action = "Read the object's available properties and select one it supports."
	case service.ErrorCodeReadAccessDenied:
		failure.Message = "The device does not allow this property to be read."
		failure.Action = "Check the device's access settings or select a readable property."
	case service.ErrorCodeWriteAccessDenied:
		failure.Message = "The device does not allow this property to be changed in its current state."
		failure.Action = "Check the property's Access guidance and the device's permissions or operating mode."
	case service.ErrorCodeValueOutOfRange, service.ErrorCodeParameterOutOfRange:
		failure.Message = "The device rejected a value outside its allowed range."
		failure.Action = "Check the property's limits and enter a supported value."
	case service.ErrorCodeInvalidDataType, service.ErrorCodeDatatypeNotSupported:
		failure.Message = "The device does not accept this value's data type."
		failure.Action = "Read the current property type and use its matching editor."
	case service.ErrorCodeInvalidArrayIndex:
		failure.Message = "The selected array item does not exist."
		failure.Action = "Read the array size and choose an item within its bounds."
	case service.ErrorCodePropertyIsNotArray:
		failure.Message = "This property is not an array."
		failure.Action = "Clear the array index and read the whole property."
	case service.ErrorCodePropertyIsNotAList:
		failure.Message = "This property cannot be read as a list range."
		failure.Action = "Use Read properties, or select a supported list or log-buffer property."
	case service.ErrorCodeNotCOVProperty:
		failure.Message = "The device does not support change notifications for this property."
		failure.Action = "Use polling in Watch & trends for this property."
	case service.ErrorCodeOptionalFunctionalityNotSupported, service.ErrorCodeUnsupportedObjectType:
		failure.Message = "The device does not support the requested feature or object type."
		failure.Action = "Select a feature or object type supported by this device."
	case service.ErrorCodeBusy, service.ErrorCodeConfigurationInProgress:
		failure.Message = "The device is busy or is applying configuration changes."
		failure.Action = "Wait for the device to finish, then try the operation again."
	case service.ErrorCodePasswordFailure, service.ErrorCodeServiceRequestDenied, service.ErrorCodeFileAccessDenied:
		failure.Message = "The device denied access to this operation."
		failure.Action = "Check the device's permissions and any credentials required for this operation."
	default:
		failure.Message = "The device reported that it could not complete this operation."
		failure.Action = "Copy the technical details and check the device documentation."
	}
	return failure
}

func changesDevice(operation string) bool {
	switch operation {
	case "write", "schedule-write", "calendar-write", "file-write", "create-object", "delete-object", "restart", "time-sync", "communication-control", "acknowledge":
		return true
	}
	return false
}
