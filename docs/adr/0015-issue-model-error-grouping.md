# ADR 0015: Issue model (error grouping, fingerprinting, resolved/regressed state machine)

* Status: Proposed
* Date: 2026-10-02
* Deciders: scott
* Tracking: TOBS-16

## Context and Problem Statement

The pipeline now stores spans and logs stamped with Project / Service / Environment / Version (ADR
0004/0007). But a stream of individual error occurrences is a log pile, not an error monitor. The core
product value (architecture doc, "App feature inventory") is the **Issue**: collapse the N occurrences
of the same bug into one record with a count, first/last-seen, the affected versions, and a status, and
make "resolved in version N, regressed in N+2" computable.

This ADR decides what an Issue is, how occurrences are grouped (fingerprinting), where it is computed,
and the status state machine.

## Decision Drivers

* **Grouping must be stable and language-agnostic.** The same bug across many occurrences and languages
  must land on one Issue; the fingerprint works on OTLP data regardless of source (ADR 0009).
* **Resolved/regressed must be computable, not guessed.** The server-assigned monotonic Version sequence
  (ADR 0008) is the ordering that makes "regressed in a later version" decidable.
* **Compute where the domain logic already lives.** The evaluator already resolves entities and sees
  every admitted event; the Issue projection belongs there, not in a separate service.
* **Human status is authoritative; recurrence is observed.** Resolve/Ignore are human acts; Regressed is
  derived from new occurrences, never set by hand.

## Decision

### 1. The Issue entity

An `Issue` is a per-(Project, Service) document keyed by a fingerprint:

* identity: `ProjectId`, `ServiceId`, `Fingerprint` (unique together);
* grouping display: `Title`, `ErrorType`;
* lifecycle: `Status` (`Unresolved` / `Resolved` / `Ignored` / `Regressed`);
* counts/time: `Count`, `FirstSeenAtUtc`, `LastSeenAtUtc`;
* version axis (ADR 0008): `FirstSeenVersionSequence`, `LastSeenVersionSequence`,
  `ResolvedInVersionSequence?`, and the set of `AffectedVersionSequences`.

### 2. Error signals

An Issue occurrence is derived from admitted telemetry that denotes an error:

* a span with `StatusCode == 2` (ERROR), or
* a log record with `SeverityNumber >= 17` (ERROR and above).

Both may carry exception detail in attributes (`exception.type`, `exception.message`), per ADR 0009's
attribute-carried exception richness.

### 3. Fingerprint

The fingerprint is a stable hash over `ServiceId` plus a **grouping key**, chosen in priority order:

1. `exception.type` (plus a normalized message / top frame when present), else
2. the span `Name` + status message, or the log's normalized body.

It is deterministic and language-agnostic (works on OTLP attributes, not per-language SDK internals).

### 4. Projection on admit (upsert by natural key)

The evaluator, after promoting telemetry, projects error signals into Issues in the same admit pass:
resolve-or-create by `(ProjectId, ServiceId, Fingerprint)`, then

* **new:** `Status = Unresolved`, `Count = 1`, first/last-seen set, `FirstSeenVersionSequence` set;
* **existing:** increment `Count`, update `LastSeen*`, union the version into `AffectedVersionSequences`;
* **regression:** if `Status == Resolved` and the occurrence's version sequence `> ResolvedInVersionSequence`,
  flip to `Regressed`.

Occurrences are aggregated within a single event (one Issue incremented once per distinct fingerprint per
admit) before the write, mirroring the entity resolver (ADR 0004).

### 5. Status transitions

`Unresolved -> Resolved` / `Ignored` are human actions (recorded with the resolving version, which sets
`ResolvedInVersionSequence`). `Resolved -> Regressed` is observed (section 4). `Regressed` is treated as
active (like Unresolved) for alerting.

## Consequences

### Positive

* Occurrences collapse into actionable Issues with counts, timing, and affected versions.
* Resolved/regressed is decidable from the Version sequence, not heuristics.
* No new service: the projection rides the existing admit pass and the capability store.

### Negative / accepted costs

* **Fingerprint quality is iterative.** The first grouping key is coarse; symbolication and stack-frame
  normalization (separate work) will refine it. Accepted: a coarse-but-stable fingerprint beats no Issue.
* **Projection adds work to the hot admit path.** Bounded: one upsert per distinct fingerprint per event,
  cached in-event like entity resolution.

### Neutral / future-facing

* Span **events** of type `exception` (richer than status+attributes) are not parsed yet (the trimmed OTLP
  schema stops at span bodies); when added they become the preferred fingerprint source.
* Symbolication / source maps, alerting on new/regressed Issues, and the Issue UI are their own items.

## Links

* Version sequence that makes resolved/regressed computable: ADR 0008 (TOBS-8).
* Attribute-carried exception richness and the OTLP boundary: ADR 0009 (TOBS-9).
* Where the projection runs (the evaluator admit path): ADR 0004 (TOBS-4).
* Stored telemetry the signals come from: ADR 0004 / `IngestedSpan` / `IngestedLog`.
* Originating design note: `docs/holdfast-architecture.md`, "App feature inventory" (Issue model).
