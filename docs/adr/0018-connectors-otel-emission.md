# ADR 0018: Connectors are thin OTel wiring aligned to tamp's diagnostics-emission contract

* Status: Proposed
* Date: 2026-10-02
* Deciders: scott
* Tracking: TOBS-21

## Context and Problem Statement

tamp-observer needs client connectors so real applications can send telemetry in, starting with .NET (and
Blazor next, TOBS-22). The question is what a connector *is*: a bespoke SDK with its own wire format, or a thin
adapter over something standard.

Two upstream facts decide most of it. First, ingestion is pure OTLP (ADR 0009): the collector speaks OTLP and
nothing else, and the admit path keys on a `tamp.project.key` resource attribute (ADR 0004/0007). Second, the
tamp build framework already defines a diagnostics-emission contract (tamp ADR 0018): Tamp.Core emits
`System.Diagnostics.ActivitySource` spans and `System.Diagnostics.Metrics.Meter` metrics from the BCL with no
third-party telemetry dependency, and consumers subscribe via OpenTelemetry, mapping tags to OTel resource
attributes at subscription time. That ADR explicitly anticipates a thin satellite package that adds `AddTamp()`
to the OTel provider builders.

A tamp-observer connector should be that satellite, not a parallel invention.

## Decision Drivers

* **Ingestion is OTLP, so connectors emit OTLP.** No custom wire format; the collector is the one front door.
* **Do not re-implement telemetry.** OpenTelemetry .NET already does traces/logs/metrics and OTLP export; a
  connector is wiring, not a telemetry stack.
* **Honor tamp ADR 0018.** The .NET ecosystem already emits `Tamp.Build*` ActivitySources with a stable tag
  contract; the connector subscribes to them and maps the identity tags, rather than defining its own.
* **The trust-root identity must ride on the telemetry.** `tamp.project.key` has to be a resource attribute or
  the evaluator quarantines everything (ADR 0004/0007).

## Decision

### 1. A connector is a thin OpenTelemetry wiring package

The .NET connector is a small package over OpenTelemetry: it configures the `TracerProvider`, `MeterProvider`,
and logging to export **OTLP to the tamp-observer collector**, and nothing more exotic. It takes a hard
dependency on `OpenTelemetry.*` (unlike Tamp.Core, which must not); a connector is opt-in integration code, so
the dependency is appropriate here.

### 2. Identity mapping is the connector's job

The connector sets the OTel resource from connector options:

* `tamp.project.key` (required): the trust-root ingestion identity (ADR 0007); without it the evaluator
  quarantines the event.
* `service.name` / `service.namespace`: the logical project and area, mirroring tamp ADR 0018's
  `[BuildProject(Name=, Area=)]` mapping.
* `service.version` from Version, `deployment.environment` from Environment (the discovered entity axes, ADR 0007).

### 3. It subscribes to the tamp build sources (ADR 0018 alignment)

The connector calls `AddSource("Tamp.Build*")` and adds the `Tamp.Build` meter, so a process that runs a tamp
build (or hosts build tooling) emits that telemetry into tamp-observer with no extra wiring. This is the
`AddTamp()`-satellite role tamp ADR 0018 anticipated, realized against our collector.

### 4. First target: our own ASP.NET Core API (dogfood)

The first consumer is the tamp-observer API host itself: runtime instrumentation (ASP.NET Core, HttpClient,
runtime metrics) plus the identity mapping, exported to the persistent dogfood instance (TOBS-20). We observe
ourselves with our own stack before shipping the connector outward.

### 5. Off by default, enabled by configuration

The connector is wired only when a collector endpoint and project key are configured, so the floor (which may
run no collector reachable from the API process, or none at all) is unaffected. No endpoint, no exporter.

## Consequences

### Positive

* Connectors are small and standard: OTel idioms, OTLP wire, no bespoke SDK to maintain.
* tamp build telemetry flows in for free via the `Tamp.Build*` subscription, honoring the upstream contract.
* The identity mapping keeps the trust-root model (ADR 0007) intact end to end.

### Negative / accepted costs

* The connector depends on `OpenTelemetry.*`. Accepted: it is opt-in integration code, not framework core.
* OTel wiring is awkward to unit-test; correctness is proven by live export into the dogfood instance plus
  focused option/resource-mapping tests.

### Neutral / future-facing

* Packaging as a satellite repository (tamp ADR 0026) is possible later; for now the connector lives here so it
  can be dogfooded against the API in one repo.
* The Blazor connector (TOBS-22) reuses the identity mapping and adds the browser archetype (rrweb + JS errors +
  the session-id correlation header).

## Links

* OTLP-only ingestion and the session front door: ADR 0009 (TOBS-9).
* The trust-root `tamp.project.key` and discovered entity axes: ADR 0004 (TOBS-4), ADR 0007 (TOBS-7).
* Upstream diagnostics-emission contract this aligns to: tamp ADR 0018.
* The persistent dogfood target: TOBS-20.
