# ADR 0004: Dumb collector, smart .NET evaluator, with a transient raw bucket between them

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-4

## Context and Problem Statement

ADR 0003 fixed that a Go OTel Collector terminates OTLP, proves it is well-formed, lands opaque bytes,
and acks, with no domain logic on that hot path. That leaves the other half of the pipeline unspecified:
where does all the intelligence go, and how does an event get from "landed raw bytes" to "a row in the
store" or "a rejected event with a reason"?

"Well-formed OTLP" says nothing about whether the data is wanted: whether it belongs to a supported
project, whether required fields are present, which Service / Environment / Version it resolves to. That
adjudication needs database access, entity resolution, and caching. It is domain logic, it is stateful,
and in a .NET house it belongs in .NET, not bolted onto the Go pipe.

This ADR decides the shape of the pipeline from the collector's hand-off onward: the seam between
collector and evaluator (the raw bucket), where domain logic lives (the .NET evaluator), the verdicts it
produces, and where rejected events go (quarantine). The storage engines that admitted events land in
are ADR 0005; the write/read abstraction over them is ADR 0006.

## Decision Drivers

* **The hot path must stay dumb (ADR 0003).** Anything the collector does is latency every event pays.
  Domain logic cannot live there without reintroducing the cost we just removed.
* **Domain logic is stateful and .NET-shaped.** Supported-project checks, field validation, and
  Project/Service/Environment/Version resolution all need the database and caches. That is the .NET side.
* **Untrusted input posture.** Everything that lands is well-formed-but-untrusted. The pipeline must be
  able to reject without crashing and without losing the rejected event.
* **Air-gap debuggability.** You cannot attach a live debugger to a customer's air-gapped site. Rejected
  events must be durable, queryable, and carry a reason, or a silent site is uninvestigable.
* **Backpressure without a filing cabinet.** The buffer between a bursty, fast collector and a stateful
  evaluator is a flow shock absorber, not storage. It must be chosen for flow, and it must not let
  events pile up indefinitely.

## Invariants

1. **Every event leaves raw for exactly one of {store, quarantine}.** Nothing lingers in raw. Raw is
   transient by definition; an event is either promoted or rejected, never parked.
2. **The collector stays dumb.** No domain logic migrates back onto the Go path. If something needs
   project context, field semantics, or entity state, it is evaluator work.
3. **A reject is never a crash and never a silent drop.** A rejected or unparseable event becomes a
   verdict that lands it in quarantine with a reason.

## Decision

### 1. The pipeline shape

```
agents -> Go OTel collector (receive + land raw) -> raw bucket (transient)
       -> .NET evaluator (classify / resolve / admit) -> tiered store
                                                   \-> quarantine / archive (rejects)
```

The collector (ADR 0003) lands opaque OTLP bytes plus a minimal envelope into the raw bucket and acks.
The evaluator consumes from the raw bucket. Admitted events are promoted to the store (ADR 0005);
rejected events go to quarantine. Nothing else sits on the hot path.

### 2. The raw bucket is a transient flow buffer, tiered upward-only

The raw bucket is the shock absorber between the bursty collector and the stateful evaluator. It is
chosen for flow, not custody (custody is quarantine's job, below). It is tiered on the same
upward-only principle as the store (ADR 0005):

* **Low / default tier (no new infra):** an in-process bounded queue (`System.Threading.Channels`) or a
  consumed-and-deleted Postgres "pending" table (rows deleted on consume, so minimal vacuum churn). This
  is what the floor install runs; it drags in nothing beyond Postgres.
* **High tier:** Valkey Streams as a durable fast landing buffer, drained with consumer-group ack.
  Valkey, not Redis, for the licensing reasons that run through this whole project (a drop-in BSD fork
  with no license gate to audit).

This subsumes what the handoff doc first described as a separate "buffering tier": there is no separate
buffer stage; the raw bucket **is** the buffer. Keeping it a config tier (not a fork in the code) is
what ADR 0006's write abstraction has to preserve.

### 3. The .NET evaluator owns all domain intelligence

The evaluator consumes from the raw bucket and treats everything in it as well-formed-but-untrusted. It
performs:

* **supported-project check** (reject unknown projects to quarantine; Project is a human-granted trust
  root, so it is never auto-created here, see the entity-model ADR);
* **required-field validation;**
* **Project / Service / Environment / Version resolution and upsert** (Service and Version auto-register
  on first sight; resolution is cached so a Version seen a million times is not a DB round-trip per
  event).

This is the only place domain logic lives. The hot collector path has none of it.

### 4. Three verdicts

The evaluator emits exactly one of:

* **admit**: valid and supported; promote to the store.
* **reject**: invalid or unsupported; send to quarantine with a reason.
* **provision-then-admit**: valid but needs a side effect first (e.g. auto-create the Version), then
  admit. This is the path that makes Service/Version auto-registration work without a human in the loop.

### 5. Quarantine is a separate, durable, queryable custody store

Quarantine is distinct from the raw bucket: raw is transient flow, quarantine is durable custody.
Rejected events sit in quarantine with their reason, inspectable. The baseline implementation is a
Postgres table (with cheap object/file storage as an option for the raw bytes). This is the thing that
makes an air-gapped site you cannot interrogate live still debuggable after the fact.

## Consequences

### Positive

* The latency win from ADR 0003 holds: the hot path stays dumb, and all the expensive, stateful work is
  off it in .NET where the database and caches are.
* The floor install needs nothing but Postgres: the default raw-bucket tier is in-process or a Postgres
  pending table, and quarantine is a Postgres table. No Valkey until a site dials up.
* Untrusted input is handled honestly: nothing crashes the pipeline, and nothing is dropped silently.
* Air-gapped sites stay investigable through queryable quarantine, which is the only debugging surface
  available when you cannot attach live.

### Negative / accepted costs

* **Two durable stores to reason about (raw and quarantine) beyond the system of record.** Accepted:
  they have genuinely different lifetimes (transient vs custody) and conflating them would either turn
  raw into a filing cabinet or make rejects ephemeral, both of which we explicitly reject.
* **The resolution cache is correctness-sensitive.** Caching Project/Service/Version resolution to avoid
  per-event DB round-trips means cache-invalidation on entity changes is now a real concern the
  evaluator owns. Accepted: the per-event round-trip alternative does not survive ingest volume.

### Neutral / future-facing

* The evaluator's standing workloads (e.g. per-Service+Version error-rate baselines for capture
  triggers) attach here but are specified in the capture-policy ADR (TOBS-12), not this one.
* Whether Native AOT helps any evaluator sub-path is not pursued; with Go owning ingestion the hot-path
  AOT question is moot (ADR 0003), and the evaluator is reflection-friendly (Marten) anyway.

## Links

* The dumb collector this complements: ADR 0003 (TOBS-3).
* The tiered store that admitted events land in: storage-tiering ADR (TOBS-5, planned).
* The write/read abstraction that keeps raw-bucket and store tiers a config flag, not a fork: ADR 0006
  (TOBS-6, planned).
* Entity resolution rules (trust-root Project, auto-register Service/Version, dedup/cache): entity-model
  ADR (TOBS-7, planned).
* Standing baseline workloads that ride the evaluator: capture-policy ADR (TOBS-12, planned).
* Originating design note: `docs/holdfast-architecture.md`, "Pipeline", "Raw bucket", "Evaluator",
  "Quarantine / archive".
