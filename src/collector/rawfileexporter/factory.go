// Package rawfileexporter is tamp-observer's thin raw-landing exporter (ADR 0004): it lands
// opaque OTLP bytes plus a minimal envelope into a spool directory and acks. It is the first
// concrete implementation of the "thin exporter that lands the raw payload durably and acks"
// from ADR 0003; the Postgres-pending and Valkey-stream raw-bucket tiers (ADR 0004 section 2)
// are other landing targets added later behind the same shape.
package rawfileexporter

import (
	"context"

	"go.opentelemetry.io/collector/component"
	"go.opentelemetry.io/collector/exporter"
	"go.opentelemetry.io/collector/exporter/exporterhelper"
)

// componentType is the config key / component id for this exporter.
var componentType = component.MustNewType("rawfile")

// stability: this is a development-grade spike component.
const stability = component.StabilityLevelDevelopment

// NewFactory builds the rawfile exporter factory for all three signals.
func NewFactory() exporter.Factory {
	return exporter.NewFactory(
		componentType,
		createDefaultConfig,
		exporter.WithTraces(createTraces, stability),
		exporter.WithMetrics(createMetrics, stability),
		exporter.WithLogs(createLogs, stability),
	)
}

func createTraces(ctx context.Context, set exporter.Settings, cfg component.Config) (exporter.Traces, error) {
	e := newRawExporter(cfg.(*Config), set)
	return exporterhelper.NewTraces(ctx, set, cfg, e.pushTraces,
		exporterhelper.WithStart(e.start))
}

func createMetrics(ctx context.Context, set exporter.Settings, cfg component.Config) (exporter.Metrics, error) {
	e := newRawExporter(cfg.(*Config), set)
	return exporterhelper.NewMetrics(ctx, set, cfg, e.pushMetrics,
		exporterhelper.WithStart(e.start))
}

func createLogs(ctx context.Context, set exporter.Settings, cfg component.Config) (exporter.Logs, error) {
	e := newRawExporter(cfg.(*Config), set)
	return exporterhelper.NewLogs(ctx, set, cfg, e.pushLogs,
		exporterhelper.WithStart(e.start))
}
