// Package valkeystreamexporter lands raw OTLP into a Valkey Stream (XADD) as the raw-bucket high tier
// (ADR 0004 section 2). It is the write-side counterpart of the .NET ValkeyRawBucketReader; both agree
// on the stream field layout (envelope + payload). Entities and admission are the evaluator's job; this
// exporter stays dumb (ADR 0003/0004).
package valkeystreamexporter

import (
	"context"

	"go.opentelemetry.io/collector/component"
	"go.opentelemetry.io/collector/exporter"
	"go.opentelemetry.io/collector/exporter/exporterhelper"
)

var componentType = component.MustNewType("valkeystream")

const stability = component.StabilityLevelDevelopment

// NewFactory builds the valkeystream exporter factory for all three signals.
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
	e := newExporter(cfg.(*Config), set)
	return exporterhelper.NewTraces(ctx, set, cfg, e.pushTraces,
		exporterhelper.WithStart(e.start), exporterhelper.WithShutdown(e.shutdown))
}

func createMetrics(ctx context.Context, set exporter.Settings, cfg component.Config) (exporter.Metrics, error) {
	e := newExporter(cfg.(*Config), set)
	return exporterhelper.NewMetrics(ctx, set, cfg, e.pushMetrics,
		exporterhelper.WithStart(e.start), exporterhelper.WithShutdown(e.shutdown))
}

func createLogs(ctx context.Context, set exporter.Settings, cfg component.Config) (exporter.Logs, error) {
	e := newExporter(cfg.(*Config), set)
	return exporterhelper.NewLogs(ctx, set, cfg, e.pushLogs,
		exporterhelper.WithStart(e.start), exporterhelper.WithShutdown(e.shutdown))
}
