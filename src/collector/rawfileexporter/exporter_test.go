package rawfileexporter

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestLandWritesPayloadThenEnvelope(t *testing.T) {
	dir := t.TempDir()
	e := &rawExporter{cfg: &Config{Directory: dir, Source: "otlp"}}

	payload := []byte("opaque-otlp-bytes")
	if err := e.land("traces", payload); err != nil {
		t.Fatalf("land: %v", err)
	}

	var otlpPath, envPath string
	entries, _ := os.ReadDir(dir)
	for _, en := range entries {
		switch {
		case strings.HasSuffix(en.Name(), ".traces.otlp"):
			otlpPath = filepath.Join(dir, en.Name())
		case strings.HasSuffix(en.Name(), ".traces.json"):
			envPath = filepath.Join(dir, en.Name())
		}
	}
	if otlpPath == "" || envPath == "" {
		t.Fatalf("expected payload + envelope files, got %v", entries)
	}

	gotPayload, _ := os.ReadFile(otlpPath)
	if string(gotPayload) != string(payload) {
		t.Fatalf("payload mismatch: %q", gotPayload)
	}

	var env envelope
	b, _ := os.ReadFile(envPath)
	if err := json.Unmarshal(b, &env); err != nil {
		t.Fatalf("envelope unmarshal: %v", err)
	}
	if env.Signal != "traces" || env.Source != "otlp" || env.Format != "otlp-proto" {
		t.Fatalf("unexpected envelope: %+v", env)
	}
	if env.PayloadBytes != len(payload) {
		t.Fatalf("payload_bytes = %d, want %d", env.PayloadBytes, len(payload))
	}
	if env.ReceiptID == "" || env.ReceivedAt == "" {
		t.Fatalf("missing receipt id or received-at: %+v", env)
	}
	if env.PayloadFile != filepath.Base(otlpPath) {
		t.Fatalf("payload_file = %q, want %q", env.PayloadFile, filepath.Base(otlpPath))
	}
}

func TestReceiptIDsAreUnique(t *testing.T) {
	seen := map[string]bool{}
	for i := 0; i < 100; i++ {
		id, err := receiptID()
		if err != nil {
			t.Fatalf("receiptID: %v", err)
		}
		if len(id) != 32 {
			t.Fatalf("receipt id length = %d, want 32", len(id))
		}
		if seen[id] {
			t.Fatalf("duplicate receipt id %q", id)
		}
		seen[id] = true
	}
}

func TestConfigValidate(t *testing.T) {
	if err := (&Config{Directory: "spool"}).Validate(); err != nil {
		t.Fatalf("valid config rejected: %v", err)
	}
	if err := (&Config{Directory: ""}).Validate(); err == nil {
		t.Fatal("empty directory should be rejected")
	}
}

func TestDefaultConfig(t *testing.T) {
	c, ok := createDefaultConfig().(*Config)
	if !ok {
		t.Fatal("createDefaultConfig did not return *Config")
	}
	if c.Directory == "" || c.Source == "" {
		t.Fatalf("default config has empty fields: %+v", c)
	}
}
