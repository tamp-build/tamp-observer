# ADR 0005: Storage tiering (Postgres/Marten, DuckDB, ClickHouse), upward-only

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-5

## Context and Problem Statement

Admitted events (ADR 0004) have to land somewhere queryable. The audience span (ADR 0001) makes a single
storage engine untenable: the floor is a dev shop whose whole database should be quiet at idle and
trivial to run, and the ceiling is a high-volume site doing p95-over-30-days across hundreds of millions
of rows. No one engine is both "quiet and trivial at the floor" and "fast columnar at the ceiling"
without cost.

So storage is a tier, chosen per site on data volume, not a fixed pick. This ADR decides the engines, the
trigger for moving between them, which engine is the system of record, and the migration ratchet that
keeps tier moves one-way and safe. It builds on ADR 0004's promise that the raw-bucket/stream seam makes
standing up a new tier a backfill-and-catch-up, not a stop-the-world migration.

## Decision Drivers

* **Floor must be quiet and trivial.** The default store has to run on nothing but Postgres, be quiet at
  idle, and need no second system. A dev shop should never pay an idle tax.
* **Ceiling must be columnar-fast.** Large-range aggregations and sustained high-volume ingest are a
  structural weakness of row stores, not an index gap. The top tier has to be columnar.
* **Accreditation and no-reachback.** Engines must be on approved-products lists where possible, be
  license-clean (no license gate = no call-home vector to audit), and never phone home.
* **.NET-house ergonomics.** The baseline should feel first-class in .NET and give us the freeform-JSON
  document queries the product leans on, plus an event-store shape that suits an event pipeline.
* **Migrations must be safe and one-way.** The downward path (columnar back to relational) is a
  quarter-eating reconciliation we refuse to build. Tier moves go up only.

## Considered Options (system of record)

1. **Postgres + Marten** as baseline system of record. Quiet at idle, accreditable, license-clean, no
   reachback, first-class .NET. ← chosen baseline.
2. **ClickHouse** as the single store. Columnar-fast but has a real idle tax (continuous background
   merge/compaction, observed ~10% CPU even on a quiet node) and is overkill at the floor. ← chosen only
   as an opt-in top tier, never the default.
3. **DuckDB** as the only analytical engine. Embedded, MIT, zero idle, columnar, but not an always-hot
   ingest target. ← chosen as an on-demand accelerator between the other two, not a tier of record.
4. **RavenDB.** Loved for freeform JSON and fast queries. ← rejected; see below.

## Decision

### 1. Baseline / system of record: Postgres + Marten

The default, and the only thing a floor install runs. Postgres is quiet at idle, on every approved-
products list (defense teams know how to ATO it), license-clean, no reachback, and first-class in .NET via
Npgsql/Marten. Marten gives the RavenDB-like freeform-JSON document ergonomics we actually wanted (jsonb +
GIN indexing + LINQ + promote-fields-to-columns) plus a built-in event store that suits an event pipeline.
This is where "freeform JSON, fast document queries" now lives.

Postgres does **not** fail at point lookups, recent-slice reads, or the document-y "this error and
everything attached" queries. Those are the common case, and the baseline serves them well.

### 2. On-demand accelerator: DuckDB

Below ClickHouse scale, DuckDB (MIT, embedded, columnar, zero idle) is the analytical accelerator.
Postgres stays the quiet system of record; DuckDB runs heavy columnar aggregations on demand (over
Parquet exports or straight from Postgres) and goes idle again. It earns its place precisely because it
has no idle cost: it accelerates the occasional heavy query without turning the site into a two-always-on-
engine deployment.

### 3. Opt-in top tier: ClickHouse

When per-site ingest is high enough that you want an always-hot columnar ingest target rather than an
on-demand accelerator, ClickHouse (Apache 2.0, cleanest air-gap story, best columnar speed) is the top
tier. It is opt-in, never the default, for one concrete reason: its continuous background
merge/compaction means a real idle CPU cost (observed ~10% continually) even on a quiet node. That is the
columnar-OLAP tax; the read speed is bought with that background work. A small quiet site must never be
on it. (Dev mitigation: disable the `system.*_log` tables.)

### 4. The tier trigger is data-volume-per-site

Postgres fails relative to ClickHouse specifically at: full-scan aggregations over large row counts
(p95-over-30-days, group-by across tens-to-hundreds of millions of rows), sustained high-volume ingest
(row writes + index maintenance + autovacuum churn), compression/storage footprint at high retention, and
high-cardinality group-bys at scale. All four are driven by volume. So the trigger to move up a tier is
per-site data volume, not a feature the operator wants.

### 5. RavenDB is rejected

RavenDB was loved (freeform JSON, fast queries, ahead of its time) but fails this specific job:

* it is a document store, not columnar, so it is weak on exactly the analytical aggregations that drive
  the top tier;
* its encryption-at-rest was a paid tier (moot for us anyway, since we do encryption at the volume layer,
  ADR 0001, but telling);
* AGPL/commercial server with an OEM-licensing and offline-activation question for a distributed,
  air-gapped product (a call-home/licensing vector we will not carry);
* RQL is the odd-one-out that fights a SQL-shaped provider abstraction (ADR 0006) hardest.

Marten-on-Postgres recovers the ergonomics we wanted without the licensing, analytical, or abstraction
costs. (RavenDB still shines as a whole-app single-ACID multi-model platform; just not as one
interchangeable provider in a tiered matrix.)

### 6. Migrations are an upward-only ratchet

* **Up only.** We never build the downward path. Downgrade is made **illegal in the config model**, not
  merely undocumented, because a downgrade implies the reverse migration we refuse to build.
* **The write model is a superset both engines accept from day one.** The canonical event schema is
  designed against the more-constrained target (ClickHouse column types, weaker joins) even while a site
  is still on Postgres, so a later backfill is a copy, not a transform.
* **Cutover is backfill-and-catch-up, not stop-the-world.** Stand up the new tier, backfill history,
  dual-write / parallel-run until caught up and verified, then flip reads. ADR 0004's raw-bucket/stream
  seam is what makes this painless: point a second consumer at the new store to backfill and catch up.

### 7. Open sub-decision: which tier is canonical at cutover

One decision is deliberately left open here because it changes what "migrate up" means and wants your
call:

* **(A) Postgres stays canonical**, ClickHouse is a derived, always-rebuildable read tier. Upgrades are
  cheap and reversible (you can always rebuild ClickHouse from Postgres). Cost: you keep paying for
  Postgres at the ceiling.
* **(B) ClickHouse becomes canonical at cutover.** Cheaper storage at the ceiling, but the upgrade is
  truly one-way and you own a real backfill and a canonical move.

**Recommendation: (A)**, because it keeps every tier move reversible-in-practice and matches the
upward-only-ratchet spirit (you never have to migrate *down*, because the lower tier is still the truth).
Flagged for your decision; it does not block the baseline, and the superset write-model (§6) is required
either way.

## Consequences

### Positive

* The floor runs on Postgres alone, quiet at idle, with document ergonomics and an event store, and drags
  in nothing else.
* The ceiling gets columnar speed when volume actually demands it, with the idle tax paid only by sites
  that opted in.
* DuckDB covers the large middle: heavy queries accelerated on demand with zero standing cost.
* Tier moves are one-way and safe by construction; the reverse migration is never built and never needed.

### Negative / accepted costs

* **Up to three engines across the fleet** (Postgres always; DuckDB on demand; ClickHouse at the top).
  Accepted: no single engine spans the floor-to-ceiling range without either an idle tax at the floor or
  an analytical wall at the ceiling.
* **The superset write-model is a standing constraint.** Designing every event schema against ClickHouse's
  constraints while most sites run Postgres is discipline paid continuously. Accepted: it is what turns a
  cutover into a copy instead of a transform.

### Neutral / future-facing

* Per-site realistic volume and retention numbers (which set where the ClickHouse line actually falls, vs
  staying on DuckDB-on-demand) are not yet known; they are an input to be gathered, tracked on this
  ticket, not a blocker for the baseline.
* Tier scope (per-Project vs per-Project-and-Service) is settled in the storage-abstraction ADR 0006
  alongside the raw-bucket tier scope, since both are the same config-shape question.

## Links

* What lands in this store and how cutover rides the stream seam: ADR 0004 (TOBS-4).
* The write/read abstraction that makes these engines interchangeable tiers: ADR 0006 (TOBS-6, planned).
* Volume-layer encryption and no-reachback constraints this honors: ADR 0001.
* Originating design note: `docs/holdfast-architecture.md`, "Storage tiering" and "Migration rules".
