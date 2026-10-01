# ADR 0003: Ingestion via a custom Go OpenTelemetry Collector distribution

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-3

## Context and Problem Statement

Every signal tamp-observer ingests (traces, spans, metrics, logs, and exception data) arrives as OTLP.
Something has to terminate OTLP over the wire, prove the bytes are well-formed, and get them into the
system durably and fast, under the ingest spikes an observability platform sees. This is the hottest,
most latency- and footprint-sensitive component in the whole product, and on a scale-out it is the one
that cold-starts.

The naive option is to write our own OTLP receiver in .NET, inside the same process as everything else.
That couples the hot ingest path to the JIT-warmup and footprint profile of the .NET host, and it means
re-implementing OTLP transport handling that already exists, is battle-tested, and tracks the spec.

This ADR decides **what ingests OTLP and how it is built**. It deliberately does not decide where domain
logic lives or how raw events are adjudicated; that boundary is ADR 0004 (TOBS-4).

## Decision Drivers

* **Cold-start and footprint are first-order here.** The ingest tier is what scales out under load. A
  component with millisecond start and a small static footprint removes the autoscale cold-start fear by
  construction; a JIT-warmup component reintroduces it exactly where it hurts.
* **Do not re-implement OTLP.** OTLP receivers, gRPC and HTTP/protobuf handling, and spec conformance
  already exist and are maintained. Rebuilding them is cost with no differentiation.
* **Keep the hot path dumb.** The ingest component should do as little as possible: anything it does on
  the hot path is latency every event pays. Domain intelligence belongs off the hot path.
* **Air-gap and accreditation friendliness.** A single static binary with no runtime dependencies and no
  call-home is the easiest artifact to ship into and audit inside an enclave.
* **Build reliability.** Go-on-Windows has known pain (EDR/Defender interception of the toolchain, CGO
  and MinGW friction). The deploy target is Linux pods anyway.

## Decision

### 1. Build on the real OpenTelemetry Collector, assembled with `ocb`

Ingestion is a **custom OpenTelemetry Collector distribution**, assembled with `ocb` (the OpenTelemetry
Collector Builder). We do not write a from-scratch receiver; we compose a distribution from the
upstream Collector's components plus a thin exporter of our own. This gives us the standard OTLP
receivers and spec-tracking for free, and confines our custom code to the one piece that is actually
ours (the hand-off exporter).

### 2. Go, deliberately

The distribution is Go, which is the native language of OTel and the Collector, so the receivers and
OTLP handling are first-class and proven. Go also gives a static binary, millisecond start, and no JIT
warmup, which is precisely what the scale-out ingest tier needs. This is a considered choice, not an
inheritance: the rest of the product is .NET (the .NET-house constraint), and the ingest tier is the
one place we accept a second language because the cold-start and ecosystem-fit arguments are decisive.

### 3. The collector's job is narrow: terminate, prove well-formed, hand off, ack

The collector does only this:

* terminate OTLP (gRPC and HTTP/protobuf);
* let the standard receiver prove the payload is transport-valid, i.e. well-formed OTLP (this falls out
  of deserializing it, it is not a separate validation pass);
* land the raw payload durably via a thin exporter, and ack.

It does **no** project lookup, **no** field validation, **no** entity resolution, and **no** stamping.
It is dumb, fast, and stateless-ish. Storage and tier logic stay out of Go entirely; they live in .NET
(ADR 0004, ADR 0005). The Collector pipeline maps onto this directly: receiver -> minimal processor ->
thin exporter. Go stays a pure, fast pipe.

### 4. Raw landing format: opaque OTLP bytes plus a minimal envelope

The exporter lands the payload as unparsed, opaque OTLP bytes wrapped in a minimal envelope: a receipt
id, received-at timestamp, source, and transport metadata. The collector does not parse the body. A
parse failure is therefore never a landing-stage crash: it surfaces later as an evaluator verdict that
routes the event to quarantine (ADR 0004).

**Open sub-decision (does not block, owned by this ticket):** whether the stored bytes are re-serialized
canonical OTLP or the original wire bytes. Leaning original-wire, because it preserves a forensic /
provenance nicety that matters in ITAR contexts (what we store is exactly what arrived). To be pinned
before the exporter is built; it does not change the architecture either way.

### 5. Build in a Linux container, pure-Go

The distribution is built in a Linux container targeting Linux (the pods are Linux), with
`CGO_ENABLED=0` for a pure-Go static binary. This sidesteps the Windows Go-build pain (EDR interception,
CGO/MinGW) entirely, since that pain was environmental, not a property of the language.

## Consequences

### Positive

* The ingest tier scales out with no cold-start penalty and a small footprint, by construction.
* OTLP transport handling is upstream-maintained and spec-tracking; our custom surface is one thin
  exporter.
* A single static, no-call-home binary is the friendliest possible artifact to ship into and accredit
  inside an air-gapped enclave.
* The hot path carries zero domain logic, so ingest latency is insulated from every future change to
  classification, entity resolution, and storage.

### Negative / accepted costs

* **A second language in a .NET house.** Go is confined to the collector distribution, but it is still a
  second toolchain and accreditation surface. Accepted deliberately: it is one bounded component with a
  tiny custom surface, and the cold-start/ecosystem argument is decisive specifically here.
* **`ocb` is a build-time dependency** that pins us to the Collector's component model and release
  cadence. Accepted: that cadence is exactly what keeps OTLP handling current.

### Neutral / future-facing

* Native AOT was floated for a .NET ingestion-side service. With the Go collector owning ingestion, that
  question is effectively moot; kept in pocket, not pursued here.
* The exporter's durable landing target (the raw bucket) and its tiers are decided in the raw-bucket /
  storage ADRs, not here. This ADR fixes only that the collector lands opaque bytes + envelope and acks.

## Links

* The dumb-collector / smart-evaluator boundary this depends on: ADR 0004 (TOBS-4, planned).
* Where the landed raw bytes go and how they tier: raw-bucket / storage-tiering ADRs (TOBS-5, planned).
* Pillar constraints (air-gap, no call-home, .NET-house) this balances against: ADR 0001.
* Originating design note: `docs/holdfast-architecture.md`, "Ingestion (Go)".
