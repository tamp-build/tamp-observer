package valkeystreamexporter

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"time"

	"github.com/redis/go-redis/v9"
	"go.opentelemetry.io/collector/component"
	"go.opentelemetry.io/collector/exporter"
	"go.opentelemetry.io/collector/pdata/plog"
	"go.opentelemetry.io/collector/pdata/pmetric"
	"go.opentelemetry.io/collector/pdata/ptrace"
	"go.uber.org/zap"
)

// Stream field names shared with the .NET reader (ValkeyStream.cs).
const (
	envelopeField = "envelope"
	payloadField  = "payload"
)

// valkeyExporter XADDs each OTLP batch (canonical re-serialized bytes, see ADR 0003 section 4) plus a
// JSON envelope into a Valkey Stream. Returning nil acks the batch; any error lets the collector's
// queue/retry resend.
type valkeyExporter struct {
	cfg      *Config
	logger   *zap.Logger
	tracesM  ptrace.Marshaler
	metricsM pmetric.Marshaler
	logsM    plog.Marshaler
	client   *redis.Client
}

func newExporter(cfg *Config, set exporter.Settings) *valkeyExporter {
	return &valkeyExporter{
		cfg:      cfg,
		logger:   set.Logger,
		tracesM:  &ptrace.ProtoMarshaler{},
		metricsM: &pmetric.ProtoMarshaler{},
		logsM:    &plog.ProtoMarshaler{},
	}
}

func (e *valkeyExporter) start(ctx context.Context, _ component.Host) error {
	e.client = redis.NewClient(&redis.Options{Addr: e.cfg.Addr})
	if err := e.client.Ping(ctx).Err(); err != nil {
		return fmt.Errorf("valkeystream: ping %q: %w", e.cfg.Addr, err)
	}
	e.logger.Info("valkeystream exporter landing to stream",
		zap.String("addr", e.cfg.Addr), zap.String("stream", e.cfg.StreamKey))
	return nil
}

func (e *valkeyExporter) shutdown(_ context.Context) error {
	if e.client != nil {
		return e.client.Close()
	}
	return nil
}

func (e *valkeyExporter) pushTraces(ctx context.Context, td ptrace.Traces) error {
	payload, err := e.tracesM.MarshalTraces(td)
	if err != nil {
		return fmt.Errorf("valkeystream: marshal traces: %w", err)
	}
	return e.land(ctx, "traces", payload)
}

func (e *valkeyExporter) pushMetrics(ctx context.Context, md pmetric.Metrics) error {
	payload, err := e.metricsM.MarshalMetrics(md)
	if err != nil {
		return fmt.Errorf("valkeystream: marshal metrics: %w", err)
	}
	return e.land(ctx, "metrics", payload)
}

func (e *valkeyExporter) pushLogs(ctx context.Context, ld plog.Logs) error {
	payload, err := e.logsM.MarshalLogs(ld)
	if err != nil {
		return fmt.Errorf("valkeystream: marshal logs: %w", err)
	}
	return e.land(ctx, "logs", payload)
}

// envelope mirrors the .NET RawEnvelope JSON (ADR 0003). payload_file is unused on the stream tier.
type envelope struct {
	ReceiptID    string `json:"receipt_id"`
	ReceivedAt   string `json:"received_at"`
	Source       string `json:"source"`
	Signal       string `json:"signal"`
	Format       string `json:"format"`
	PayloadFile  string `json:"payload_file"`
	PayloadBytes int    `json:"payload_bytes"`
}

func (e *valkeyExporter) land(ctx context.Context, signal string, payload []byte) error {
	id, err := receiptID()
	if err != nil {
		return fmt.Errorf("valkeystream: receipt id: %w", err)
	}
	env := envelope{
		ReceiptID:    id,
		ReceivedAt:   time.Now().UTC().Format(time.RFC3339Nano),
		Source:       e.cfg.Source,
		Signal:       signal,
		Format:       "otlp-proto",
		PayloadBytes: len(payload),
	}
	envJSON, err := json.Marshal(env)
	if err != nil {
		return fmt.Errorf("valkeystream: marshal envelope: %w", err)
	}

	return e.client.XAdd(ctx, &redis.XAddArgs{
		Stream: e.cfg.StreamKey,
		Values: map[string]any{
			envelopeField: string(envJSON),
			payloadField:  payload,
		},
	}).Err()
}

func receiptID() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	return hex.EncodeToString(b[:]), nil
}
