# ADR 0006: One write model, a capability-based read interface, per-engine translators

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-6

## Context and Problem Statement

ADR 0005 commits us to multiple storage engines across the fleet (Postgres/Marten, DuckDB, ClickHouse)
and to tier moves that are config changes, not rewrites. That is only achievable if the rest of the code
talks to storage through an abstraction that genuinely spans those engines. The engines share no common
query language or data model: Postgres/Marten is relational + jsonb document, DuckDB and ClickHouse are
columnar SQL dialects, and the historically-considered RavenDB spoke RQL. A single shared SQL string
shatters on exactly the queries that matter (the large-range analytical aggregations).

The naive abstraction (one entity model, one query path, lowest-common-denominator SQL) fails twice: it
either caps every engine at the weakest common dialect, or it leaks engine specifics everywhere and the
tiers stop being interchangeable. We need an abstraction that lets the write path stay uniform, lets point
and recent-slice reads stay uniform, and lets the analytical reads be expressed as intent that each engine
fulfils in its own dialect.

This ADR decides the shape of that abstraction. It does not re-decide the engines (ADR 0005) or the
pipeline (ADR 0004); it decides the seam they meet at.

## Decision Drivers

* **Tiers must be a config flag, not a fork.** Moving a site from Postgres to ClickHouse, or turning on
  the Valkey raw-bucket tier, cannot mean a different code path through the application.
* **Don't cap engines at the weakest dialect.** The whole point of the top tier is analytical speed; an
  abstraction that forbids engine-specific aggregation strategy throws that away.
* **Writes and simple reads genuinely do abstract cleanly.** Only the analytical reads leak. The
  abstraction should be uniform where it can be and capability-based only where it must.
* **Small, auditable translators beat one clever query builder.** A generic SQL builder that tries to
  cover every engine is the thing that breaks on the interesting cases and is impossible to reason about.
* **The cheapest scale-up should be the common one.** DuckDB and ClickHouse are both columnar SQL, so the
  DuckDB to ClickHouse jump should share most of its translation code.

## Decision

### 1. Two seams, not one: a write model and a read interface

Do not build one entity model with one query path. Build **one write model** and a **capability-based
read interface**, each with per-engine translators.

### 2. `IEventSink.Write(batch)`: the single write contract

All writes into the store go through one contract, `IEventSink.Write(batch)`, with identical semantics no
matter who calls it: the evaluator's admit path (ADR 0004) in steady state, and a second
backfill/parallel-run consumer during a tier cutover (ADR 0005 §6). Because the contract is identical,
buffering slots **in front of** the sink and is never a rewrite of it. That is precisely what lets the
raw-bucket tier (in-process channel vs Postgres pending table vs Valkey Streams, ADR 0004 §2) be a config
choice rather than a code fork, and what lets a cutover be "point another sink-writer at the new engine."

The write model itself is the superset schema from ADR 0005 §6: designed against the most-constrained
target (ClickHouse column types, weaker joins) so every engine can accept it and a backfill is a copy, not
a transform.

### 3. `IObservabilityStore`: capability-based reads, expressed as intent

Reads go through `IObservabilityStore`, whose operations are defined as **intent, not SQL**:
`GetLatencyPercentiles(window, groupBy)`, `TopErrorsByFrequency(window)`, `SessionsForError(id)`, and so
on. Each provider translates intent into its native dialect. There is no shared query string and no
generic cross-engine query builder; there are small per-provider translators.

* Write side and point / recent-slice reads abstract cleanly and look uniform across providers.
* The analytical reads are where a shared dialect would leak, which is exactly why those operations are
  modelled as capabilities rather than as one SQL surface.

### 4. Capability surface sized to the weakest provider, with provider extensions

The baseline capability surface is picked by the **weakest intended provider's reach**, so every tier can
satisfy the core interface. Providers may expose provider-specific extensions above that baseline for
capabilities only they have. DuckDB and ClickHouse share the most translator code (both columnar SQL), so
the common and cheapest scale-up, DuckDB to ClickHouse, is the smallest dialect jump by design.

### 5. Per-engine cutover semantics are part of the interface

For each engine, decide whether a tier switch is "new data only, old data stays queryable in place" or
"full backfill into the new engine." This choice shapes the read interface (whether a query may span two
engines during a transition), so it is captured at this seam, not left implicit. It composes with ADR
0005 §7's still-open canonical-truth decision: if Postgres stays canonical, "old stays queryable in place"
is natural; if ClickHouse becomes canonical, full backfill is implied.

### 6. Tier scope: per-Project, with per-Service as an allowed refinement

Both the storage tier (ADR 0005) and the raw-bucket tier (ADR 0004) are selected **per Project** by
default, with **per-Service** permitted as a refinement (a site with one chatty app and three quiet ones
should not be forced to put all four on the heavy tier). This is the same config-shape question for both
tiers, so it is answered once, here. The config model still enforces the upward-only ratchet (ADR 0005
§6) at whatever scope the tier is set.

## Consequences

### Positive

* Tier moves and raw-bucket tier changes are config, not forks, because both the write contract and the
  read intents are stable across engines.
* The top tier keeps its analytical speed: capability-based reads let each engine use its own strategy
  instead of a capped common dialect.
* Translators are small and auditable per engine, which is both easier to reason about and friendlier to
  accreditation than one opaque cross-engine query builder.
* The common scale-up path (DuckDB to ClickHouse) is the cheapest to implement, matching where sites
  actually move.

### Negative / accepted costs

* **N translators to maintain**, one per engine, and each new capability must be implemented on each
  intended provider. Accepted: this is strictly less work and far less risk than a universal query builder
  that tries to be correct on every engine's analytical path.
* **The capability surface is a living contract.** Adding an analytical feature means adding an intent and
  translating it everywhere it must run. Accepted: that fan-out is visible and bounded, unlike silent
  dialect leakage.

### Neutral / future-facing

* If a future provider is ever added (or RavenDB ever returns for some niche), it joins by implementing
  `IEventSink` and the `IObservabilityStore` capabilities it can, declaring extensions for the rest. The
  seam is designed for that without a rewrite.
* The concrete list of read intents is grown as the app features that need them land (dashboards, tracing
  UI, log explorer, ADRs forthcoming), not enumerated exhaustively here.

## Links

* The engines and tier trigger this abstracts: ADR 0005 (TOBS-5), including the open canonical-truth
  decision (§7 there) that §5 here composes with.
* The write callers (evaluator admit path, backfill consumer) and raw-bucket tiers that sit in front of
  the sink: ADR 0004 (TOBS-4).
* Originating design note: `docs/holdfast-architecture.md`, "The storage abstraction (what makes tiers
  real)".
