package rawfileexporter

import (
	"errors"

	"go.opentelemetry.io/collector/component"
)

// Config controls where raw OTLP batches are landed.
//
// The exporter is deliberately dumb (ADR 0003/0004): it writes opaque OTLP bytes plus a
// minimal envelope to a spool directory and acks. No project lookup, no validation, no
// entity resolution. The .NET evaluator consumes the spool.
type Config struct {
	// Directory is the spool path where payload + envelope files are written.
	Directory string `mapstructure:"directory"`
	// Source is recorded verbatim in each envelope (transport/front-door identity).
	Source string `mapstructure:"source"`
}

func createDefaultConfig() component.Config {
	return &Config{
		Directory: "raw",
		Source:    "otlp",
	}
}

// Validate implements component.ConfigValidator.
func (c *Config) Validate() error {
	if c.Directory == "" {
		return errors.New("rawfile: directory must not be empty")
	}
	return nil
}
