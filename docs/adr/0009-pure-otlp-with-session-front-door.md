# ADR 0009: Pure-OTLP ingestion, with one deliberate session/replay front door

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-9

## Context and Problem Statement

ADR 0003 built the collector around OTLP. But the product's flagship value is a Sentry-grade error
experience (structured/symbolicated stack traces, grouping, breadcrumbs, rich context) and session replay,
and OTLP does not carry all of that natively: it represents a stack trace as an opaque string, and it has
no concept of a DOM-mutation replay stream. So we have to decide how much we bend the protocol boundary.

The temptation is to add per-signal side channels (a custom exception protocol, a custom metrics path, and
so on) wherever OTLP is thin. That way lies many front doors, each its own attack surface, auth story, and
maintenance burden. The opposite temptation (force literally everything through OTLP) breaks on the browser
session archetype, whose data shape (a DOM snapshot plus a mutation/interaction stream, shipped in bursty
chunks over minutes to hours) is genuinely not OTLP-shaped.

This ADR decides the protocol boundary: what goes through OTLP, how exception richness is carried without a
second protocol, and the single explicit exception to OTLP-purity.

## Decision Drivers

* **Protect the protocol boundary.** Each front door is attack surface, an auth story, and maintenance.
  Fewer is safer and cheaper, which matters doubly for accreditation.
* **Don't lose exception richness.** The error experience is the differentiator; carrying it cannot be
  sacrificed to protocol purity.
* **Respect data shapes that genuinely differ.** The session/replay stream is not request/response
  telemetry; forcing it through OTLP would be a worse boundary, not a purer one.
* **Lean on OTel for the broad language axis.** OTel SDKs already cover traces/spans/metrics/logs across
  many languages; reusing them is free breadth.

## Decision

### 1. Pure OTLP for everything, with exception richness pushed into attributes

Everything ingests as OTLP. Where OTLP is thin (notably exception data), we **push the richness into OTLP
attributes** and **reconstruct the structure in the evaluator** (ADR 0004). A stack trace arrives as
attribute data; the evaluator turns OTLP-grade exception data into Sentry-grade structured, groupable
issues. This keeps a single protocol on the wire while still delivering the rich experience, because the
reconstruction is language-agnostic work on OTLP exception data regardless of source.

### 2. Exactly one exception: the browser/session archetype gets its own front door

The one deliberate break from OTLP-purity is the browser/session archetype (session replay, ADR 0010). Its
data shape (DOM snapshot + mutation/interaction stream, bursty chunked delivery) is not OTLP, and pretending
otherwise would distort both. It gets its own non-OTLP session endpoint.

### 3. The collector therefore has exactly two front doors

* **OTLP** (gRPC + HTTP/protobuf) for everything: traces, spans, metrics, logs, and exception data.
* **A session endpoint** for replay chunks.

Two, and only two. New signal types do not get new doors; they ride OTLP attributes and evaluator
reconstruction. The session door is the single, bounded, justified exception.

### 4. Breadth via OTel SDKs; depth via our own processing

For the broad language axis we lean on existing OTel SDKs and auto-instrumentation ("point your OTel SDK at
our endpoint"). We concentrate our own effort on the processing layer (the evaluator and store) that turns
OTLP exception data into grouped, symbolicated issues, plus a deep first-class .NET capture path. This keeps
the protocol boundary at two doors while still differentiating on the error experience.

## Consequences

### Positive

* The attack/auth/maintenance surface is two front doors, not one-per-signal, which is a direct
  accreditation and security win.
* The error experience is preserved without a second protocol: richness rides OTLP attributes and is
  rebuilt server-side.
* The session stream is handled in its natural shape instead of being distorted into OTLP.
* Broad language coverage is mostly free (OTel SDKs), letting effort concentrate on the differentiating
  processing.

### Negative / accepted costs

* **Reconstruction logic in the evaluator is now load-bearing.** Carrying exception richness as attributes
  means the evaluator must reliably rebuild structure from them. Accepted: it is the differentiator anyway,
  and it is strictly less surface than a parallel exception protocol.
* **Two doors still means two things to secure and maintain.** Accepted: the session door earns its place;
  the discipline is that it stays the *only* exception.

### Neutral / future-facing

* If a future archetype ever genuinely cannot ride OTLP attributes (none is known), adding a third door
  would require its own ADR justifying the break, exactly as the session door is justified here. The
  default answer to "can we add a front door?" is no.

## Links

* The collector and its OTLP termination this constrains: ADR 0003 (TOBS-3).
* Evaluator reconstruction of exception structure from attributes: ADR 0004 (TOBS-4).
* The session archetype that is the one exception, in depth: ADR 0010 (TOBS-10).
* Originating design note: `docs/holdfast-architecture.md`, "Capture; what we collect" and the protocol
  decision within it.
