package fakeserver

import (
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"strings"
	"testing"
)

func TestSamePeer(t *testing.T) {
	for _, pair := range [][2]string{
		{"+43 699 1234567", "0043 699 1234567"},
		{"+43 699 1234567", "0699 1234567"},
		{"0699/123-45.67", "(0699) 1234567"},
		{"MyBank", "mybank"},
		{"+436991234567", "+436991234567"},
	} {
		if !samePeer(pair[0], pair[1]) || !samePeer(pair[1], pair[0]) {
			t.Fatalf("%q and %q are the same peer", pair[0], pair[1])
		}
	}
	for _, pair := range [][2]string{
		{"+43 699 1234567", "+49 699 1234567"},
		{"+43 699 1234567", "34567"},
		{"MyBank", "OtherBank"},
		{"A1", "+431"},
	} {
		if samePeer(pair[0], pair[1]) || samePeer(pair[1], pair[0]) {
			t.Fatalf("%q and %q are different peers", pair[0], pair[1])
		}
	}
}

func TestCountParts(t *testing.T) {
	for text, want := range map[string]int{
		"x":                       1,
		strings.Repeat("x", 160):  1,
		strings.Repeat("x", 161):  2,
		strings.Repeat("x", 1530): 10,
		strings.Repeat("x", 1531): 11,
		strings.Repeat("ä", 70):   1,
		strings.Repeat("ä", 71):   2,
		strings.Repeat("ä", 670):  10,
	} {
		if got := CountParts(text); got != want {
			t.Fatalf("%d characters: got %d parts, want %d", len(text), got, want)
		}
	}
}

func TestObjectKeepsTheOrder(t *testing.T) {
	encoded := compact(Obj(
		"z", 1, "a", nil, "m", Obj("text", "<Grüße>"), "list", []any{}))
	want := `{"z":1,"a":null,"m":{"text":"<Grüße>"},"list":[]}`
	if string(encoded) != want {
		t.Fatalf("got %s, want %s", encoded, want)
	}
}

func TestCertificate(t *testing.T) {
	first, err := NewCertificate()
	if err != nil {
		t.Fatal(err)
	}
	second, err := NewCertificate()
	if err != nil {
		t.Fatal(err)
	}
	if first.Fingerprint == second.Fingerprint {
		t.Fatal("two certificates have the same fingerprint")
	}
	parsed, err := x509.ParseCertificate(first.DER)
	if err != nil {
		t.Fatal(err)
	}
	// Like the certificate of the phone it names no host.
	if len(parsed.DNSNames)+len(parsed.IPAddresses) != 0 {
		t.Fatal("the certificate names a host")
	}
	digest := sha256.Sum256(first.DER)
	plain := strings.ToLower(strings.ReplaceAll(first.Fingerprint, ":", ""))
	if plain != hex.EncodeToString(digest[:]) || len(first.Fingerprint) != 95 {
		t.Fatalf("got the fingerprint %q", first.Fingerprint)
	}
}

func TestPage(t *testing.T) {
	items := []string{"a", "b", "c", "d", "e"}
	encode := func(request map[string]any) string {
		result, failed := page(items, nil, request)
		if failed != nil {
			return failed.code
		}
		return string(compact(result))
	}
	for want, request := range map[string]map[string]any{
		`{"items":["a","b"],"nextCursor":"2"}`: {"limit": 2.0},
		`{"items":["c","d"],"nextCursor":"4"}`: {"limit": 2.0, "cursor": "2"},
		`{"items":["e"],"nextCursor":null}`:    {"limit": 2.0, "cursor": "4"},
		`{"items":[],"nextCursor":null}`:       {"cursor": "9"},
	} {
		if got := encode(request); got != want {
			t.Fatalf("%v: got %s, want %s", request, got, want)
		}
	}
	for _, request := range []map[string]any{
		{"limit": 0.0}, {"limit": 201.0}, {"limit": "2"}, {"limit": 1.5},
		{"cursor": "x"}, {"cursor": 2.0},
	} {
		if got := encode(request); got != "invalid_request" {
			t.Fatalf("%v: got %s", request, got)
		}
	}
	keyed, _ := page(items, []string{"1:a", "2:b", "3:c", "4:d", "5:e"},
		map[string]any{"limit": 2.0, "cursor": "2:b"})
	if got := string(compact(keyed)); got != `{"items":["c","d"],"nextCursor":"4:d"}` {
		t.Fatalf("got %s", got)
	}
}
