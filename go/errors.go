package smsdock

import (
	"errors"
	"fmt"
)

// Sentinel errors for errors.Is. Every error of this package matches
// ErrSMSDock; an [APIError] also matches ErrAuthentication or
// ErrLicenceRequired when its code says so.
var (
	// ErrSMSDock matches every error type of this package.
	ErrSMSDock = errors.New("smsdock: error")

	// ErrAuthentication matches an APIError with the code
	// invalid_password, locked_out or not_authenticated: the login failed
	// or the connection is no longer logged in. The client never retries
	// after this error, because every wrong password counts towards the
	// lock of the phone.
	ErrAuthentication = errors.New("smsdock: authentication failed")

	// ErrLicenceRequired matches an APIError with the code
	// licence_required: the API needs the full licence of the app.
	ErrLicenceRequired = errors.New("smsdock: licence required")
)

// APIError says that the server answered a request with an error.
//
// Code is one of the error codes of API.md, Message the text of the
// server. Details holds the other fields of the error object, for example
// {"retryAfter": 30} or {"permission": "sendSms"}.
type APIError struct {
	Code    string
	Message string
	Details map[string]any
}

// Error returns "code: message".
func (e *APIError) Error() string {
	return e.Code + ": " + e.Message
}

// RetryAfter returns the seconds to wait before the next attempt. The
// second result is false if the server named none.
func (e *APIError) RetryAfter() (seconds int, ok bool) {
	value, isNumber := e.Details["retryAfter"].(float64)
	if !isNumber {
		return 0, false
	}
	return int(value), true
}

// Permission returns the missing Android permission of a
// permission_missing error, or "".
func (e *APIError) Permission() string {
	value, _ := e.Details["permission"].(string)
	return value
}

// IsAuthentication reports whether the login failed or the connection is
// no longer logged in (invalid_password, locked_out, not_authenticated).
func (e *APIError) IsAuthentication() bool {
	switch e.Code {
	case "invalid_password", "locked_out", "not_authenticated":
		return true
	}
	return false
}

// IsLicenceRequired reports whether the API needs the full licence of the
// app (licence_required).
func (e *APIError) IsLicenceRequired() bool {
	return e.Code == "licence_required"
}

// Is makes errors.Is(err, ErrAuthentication), errors.Is(err,
// ErrLicenceRequired) and errors.Is(err, ErrSMSDock) work.
func (e *APIError) Is(target error) bool {
	switch target {
	case ErrSMSDock:
		return true
	case ErrAuthentication:
		return e.IsAuthentication()
	case ErrLicenceRequired:
		return e.IsLicenceRequired()
	}
	return false
}

// ProtocolError says that the server sent something that protocol
// version 1 does not allow.
type ProtocolError struct {
	Message string
}

// Error returns the message.
func (e *ProtocolError) Error() string { return e.Message }

// Is makes errors.Is(err, ErrSMSDock) work.
func (e *ProtocolError) Is(target error) bool { return target == ErrSMSDock }

// ConnectionInterruptedError says that the connection failed and could
// not be restored.
//
// CloseCode and CloseReason are set when the server closed the WebSocket
// with a close frame; CloseCode is 0 otherwise. Err is the underlying
// error, if any.
type ConnectionInterruptedError struct {
	Message     string
	CloseCode   int
	CloseReason string
	Err         error
}

// Error returns the message.
func (e *ConnectionInterruptedError) Error() string { return e.Message }

// Unwrap returns the underlying error.
func (e *ConnectionInterruptedError) Unwrap() error { return e.Err }

// Is makes errors.Is(err, ErrSMSDock) work.
func (e *ConnectionInterruptedError) Is(target error) bool {
	return target == ErrSMSDock
}

// CertificateMismatchError says that the server presented a certificate
// other than the pinned one.
//
// Expected and Actual are SHA-256 fingerprints as upper-case hex pairs
// separated by colons. Nothing was sent to the server.
type CertificateMismatchError struct {
	Expected string
	Actual   string
}

// Error returns a text that names both fingerprints.
func (e *CertificateMismatchError) Error() string {
	return "The certificate of the server does not match the pinned " +
		"fingerprint.\n  expected: " + e.Expected + "\n  received: " +
		e.Actual + "\nCompare the fingerprint with the Server screen of " +
		"the app. It changes when the certificate is regenerated or the " +
		"app is reinstalled."
}

// Is makes errors.Is(err, ErrSMSDock) work.
func (e *CertificateMismatchError) Is(target error) bool {
	return target == ErrSMSDock
}

// ArgumentError says that an argument is wrong. It is returned before
// anything is sent.
type ArgumentError struct {
	Message string
}

// Error returns the message.
func (e *ArgumentError) Error() string { return e.Message }

// Is makes errors.Is(err, ErrSMSDock) work.
func (e *ArgumentError) Is(target error) bool { return target == ErrSMSDock }

// TimeoutError says that the time to wait is over. It is returned by
// WaitForEvent, WaitForResult and WaitForResponse.
type TimeoutError struct {
	Message string
}

// Error returns the message.
func (e *TimeoutError) Error() string { return e.Message }

// Timeout returns true, as for the timeout errors of package net.
func (e *TimeoutError) Timeout() bool { return true }

// Is makes errors.Is(err, ErrSMSDock) work.
func (e *TimeoutError) Is(target error) bool { return target == ErrSMSDock }

func argumentError(format string, arguments ...any) *ArgumentError {
	return &ArgumentError{Message: fmt.Sprintf(format, arguments...)}
}

func protocolError(format string, arguments ...any) *ProtocolError {
	return &ProtocolError{Message: fmt.Sprintf(format, arguments...)}
}

// transportError marks a failure of the connection: the kind of error
// after which a reconnect may help. It never leaves the package.
type transportError struct {
	err         error
	closeCode   int
	closeReason string
}

func (e *transportError) Error() string { return e.err.Error() }

func (e *transportError) Unwrap() error { return e.err }

// asTransport returns the transport error in err, if it is one.
func asTransport(err error) (*transportError, bool) {
	var failure *transportError
	if errors.As(err, &failure) {
		return failure, true
	}
	return nil, false
}

// fatalError returns the error to report for a close code that forbids a
// retry, or nil.
func fatalError(failure *transportError) error {
	code, reason := failure.closeCode, failure.closeReason
	details := map[string]any{"closeCode": code}
	or := func(fallback string) string {
		if reason != "" {
			return reason
		}
		return fallback
	}
	switch code {
	case 4401:
		return &APIError{
			Code:    "not_authenticated",
			Message: or("The server closed the connection: not logged in"),
			Details: details,
		}
	case 4410:
		return &APIError{
			Code:    "not_authenticated",
			Message: or("The password was changed in the app"),
			Details: details,
		}
	case 4403:
		return &APIError{
			Code:    "licence_required",
			Message: or("The API needs the full licence of the app"),
			Details: details,
		}
	case 1009:
		return protocolError(
			"The server closed the connection: frame larger than 64 KiB")
	case 4400:
		message := "The server closed the connection: protocol error"
		if reason != "" {
			message += " (" + reason + ")"
		}
		return protocolError("%s", message)
	}
	return nil
}

// interrupted returns the error for a connection that is lost for good.
func interrupted(message string, failure *transportError) error {
	var cause string
	if failure.closeCode != 0 {
		cause = fmt.Sprintf(
			"closed by the server with code %d", failure.closeCode)
		if failure.closeReason != "" {
			cause += " (" + failure.closeReason + ")"
		}
	} else {
		cause = failure.err.Error()
	}
	return &ConnectionInterruptedError{
		Message:     message + ": " + cause,
		CloseCode:   failure.closeCode,
		CloseReason: failure.closeReason,
		Err:         failure.err,
	}
}

// final returns the error to report for a transport error that is not
// retried.
func final(message string, failure *transportError) error {
	if fatal := fatalError(failure); fatal != nil {
		return fatal
	}
	return interrupted(message, failure)
}

func apiError(fields map[string]any) *APIError {
	code := "unknown"
	if value, present := fields["code"]; present {
		code = fmt.Sprint(value)
	}
	message := "no message"
	if value, isText := fields["message"].(string); isText && value != "" {
		message = value
	}
	details := make(map[string]any)
	for key, value := range fields {
		if key != "code" && key != "message" {
			details[key] = value
		}
	}
	return &APIError{Code: code, Message: message, Details: details}
}
