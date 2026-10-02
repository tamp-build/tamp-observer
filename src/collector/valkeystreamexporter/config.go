package valkeystreamexporter

import (
	"errors"

	"go.opentelemetry.io/collector/component"
)

// Config is the YAML-configured settings block for the Valkey-stream exporter (the raw-bucket high
// tier, ADR 0004 section 2). Configured the OTel-native way: an `exporters: valkeystream: {...}` block
// unmarshaled via mapstructure. Fields can pull from the environment in the YAML via ${env:VAR}.
type Config struct {
	// Addr is the Valkey host:port.
	Addr string `mapstructure:"addr"`
	// StreamKey is the stream events are XADDed to; must match the evaluator's reader.
	StreamKey string `mapstructure:"stream_key"`
	// Source is recorded verbatim in each envelope (transport/front-door identity).
	Source string `mapstructure:"source"`
}

func createDefaultConfig() component.Config {
	return &Config{
		Addr:      "localhost:6379",
		StreamKey: "tamp.observer.raw",
		Source:    "otlp",
	}
}

// Validate implements component.ConfigValidator.
func (c *Config) Validate() error {
	if c.Addr == "" {
		return errors.New("valkeystream: addr must not be empty")
	}
	if c.StreamKey == "" {
		return errors.New("valkeystream: stream_key must not be empty")
	}
	return nil
}
