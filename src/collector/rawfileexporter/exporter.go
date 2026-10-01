package rawfileexporter

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"go.opentelemetry.io/collector/component"
	"go.opentelemetry.io/collector/exporter"
	"go.opentelemetry.io/collector/pdata/plog"
	"go.opentelemetry.io/collector/pdata/pmetric"
	"go.opentelemetry.io/collector/pdata/ptrace"
	"go.uber.org/zap"
)

// rawExporter lands each OTLP batch as two files in the spool directory: the opaque payload
// (canonical OTLP protobuf) and a JSON envelope. The envelope is written last; its presence
// is the signal that the event is complete and ready for the evaluator to consume.
//
// Note on bytes (ADR 0003 section 4): a Collector exporter receives already-deserialized pdata,
// so it can only re-serialize CANONICAL OTLP, not the original wire bytes. Original-wire capture
// would need receiver-side interception; it is out of scope for this exporter.
type rawExporter struct {
	cfg      *Config
	logger   *zap.Logger
	tracesM  ptrace.Marshaler
	metricsM pmetric.Marshaler
	logsM    plog.Marshaler
}

func newRawExporter(cfg *Config, set exporter.Settings) *rawExporter {
	return &rawExporter{
		cfg:      cfg,
		logger:   set.Logger,
		tracesM:  &ptrace.ProtoMarshaler{},
		metricsM: &pmetric.ProtoMarshaler{},
		logsM:    &plog.ProtoMarshaler{},
	}
}

func (e *rawExporter) start(_ context.Context, _ component.Host) error {
	if err := os.MkdirAll(e.cfg.Directory, 0o755); err != nil {
		return fmt.Errorf("rawfile: create spool directory %q: %w", e.cfg.Directory, err)
	}
	e.logger.Info("rawfile exporter landing to spool", zap.String("directory", e.cfg.Directory))
	return nil
}

func (e *rawExporter) pushTraces(_ context.Context, td ptrace.Traces) error {
	payload, err := e.tracesM.MarshalTraces(td)
	if err != nil {
		return fmt.Errorf("rawfile: marshal traces: %w", err)
	}
	return e.land("traces", payload)
}

func (e *rawExporter) pushMetrics(_ context.Context, md pmetric.Metrics) error {
	payload, err := e.metricsM.MarshalMetrics(md)
	if err != nil {
		return fmt.Errorf("rawfile: marshal metrics: %w", err)
	}
	return e.land("metrics", payload)
}

func (e *rawExporter) pushLogs(_ context.Context, ld plog.Logs) error {
	payload, err := e.logsM.MarshalLogs(ld)
	if err != nil {
		return fmt.Errorf("rawfile: marshal logs: %w", err)
	}
	return e.land("logs", payload)
}

// envelope is the minimal metadata written alongside each raw payload (ADR 0003):
// receipt id, received-at, source, and transport metadata (signal + format).
type envelope struct {
	ReceiptID    string `json:"receipt_id"`
	ReceivedAt   string `json:"received_at"`
	Source       string `json:"source"`
	Signal       string `json:"signal"`
	Format       string `json:"format"`
	PayloadFile  string `json:"payload_file"`
	PayloadBytes int    `json:"payload_bytes"`
}

// land writes the payload then the envelope, each atomically (temp file, fsync, rename), and
// returns nil only once both are durable. Returning nil is the ack back to the pipeline; any
// error is surfaced so the collector's queue/retry can resend.
func (e *rawExporter) land(signal string, payload []byte) error {
	id, err := receiptID()
	if err != nil {
		return fmt.Errorf("rawfile: receipt id: %w", err)
	}

	payloadName := fmt.Sprintf("%s.%s.otlp", id, signal)
	if err := writeFileSync(filepath.Join(e.cfg.Directory, payloadName), payload); err != nil {
		return fmt.Errorf("rawfile: land payload: %w", err)
	}

	env := envelope{
		ReceiptID:    id,
		ReceivedAt:   time.Now().UTC().Format(time.RFC3339Nano),
		Source:       e.cfg.Source,
		Signal:       signal,
		Format:       "otlp-proto",
		PayloadFile:  payloadName,
		PayloadBytes: len(payload),
	}
	envBytes, err := json.Marshal(env)
	if err != nil {
		return fmt.Errorf("rawfile: marshal envelope: %w", err)
	}

	// Envelope written last: its presence marks the event complete for the consumer.
	envName := fmt.Sprintf("%s.%s.json", id, signal)
	if err := writeFileSync(filepath.Join(e.cfg.Directory, envName), envBytes); err != nil {
		return fmt.Errorf("rawfile: land envelope: %w", err)
	}
	return nil
}

func receiptID() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	return hex.EncodeToString(b[:]), nil
}

// writeFileSync writes atomically: write to a temp file, fsync, then rename into place.
func writeFileSync(path string, data []byte) error {
	tmp := path + ".tmp"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return err
	}
	if _, err := f.Write(data); err != nil {
		_ = f.Close()
		return err
	}
	if err := f.Sync(); err != nil {
		_ = f.Close()
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}
