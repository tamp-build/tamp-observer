# ADR 0008: Version ordering via a server-controlled monotonic sequence

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-8

## Context and Problem Statement

ADR 0007 makes Version a first-class, auto-discovered axis and the basis for "resolved in version N,
regressed in N+2." That state machine needs a reliable **ordering** of versions. But `service.version` is
free-form: it can be a git sha, a CI build number, semver, semver-plus-build, or anything an agent emits.
There is no total order on those strings, and sorting them lexically is wrong (it would put `v10` before
`v9`, and shas have no order at all).

Worse, in an air-gapped box the agent's clock is untrusted, so we cannot order by an agent-reported
timestamp either. We need an ordering we actually own, plus rules for the messy cases (missing version,
missing instance) that do not crash the pipeline and do not silently merge distinct things.

## Decision Drivers

* **Ordering must be trustworthy and server-owned.** The one monotonic fact we control is arrival at the
  collector. Agent clocks and agent-supplied version strings cannot be trusted to order anything.
* **Never sort on the version string.** Free-form strings have no correct sort; any attempt invites subtle
  wrong answers in the resolved/regressed logic.
* **Messy input must degrade, not crash or merge.** Missing fields are normal; the pipeline must bucket
  them explicitly rather than fabricate or collapse identity.
* **Ingest volume forbids per-event DB round-trips.** A version seen a million times must resolve from
  cache, not hit the database each time.

## Decision

### 1. Assign a server-controlled monotonic sequence at first-seen arrival

Each Version gets a server-assigned monotonic sequence number, ordered by **first-seen arrival time at the
collector**. That arrival order is the one monotonic fact we own, and it is what the resolved/regressed
state machine orders on. The `service.version` string is kept for identity and display, never for sorting.

### 2. Parse semver opportunistically, for display only

Where a version string happens to be semver (or semver-plus-build), parse it opportunistically to improve
display (grouping, human-friendly labels). This never feeds ordering; it is cosmetic. Ordering is always
the monotonic sequence from §1.

### 3. Synthetic buckets for missing fields (bucket, do not crash or merge)

* **Missing / empty `service.version`**: bucket into a synthetic `unversioned` build per Service. Do not
  crash, do not silently merge into some other version.
* **Missing `service.instance.id`**: bucket into a synthetic `unknown-instance`. Do not fabricate distinct
  instances we cannot actually distinguish.

### 4. Dedup is upsert-by-natural-key, cached

Dedup/resolution is an upsert by natural key on Project -> Service -> Version. The resolution is cached, so
a Version seen a million times does not cost a DB round-trip per event. This is the cache ADR 0004 flagged
as correctness-sensitive; invalidation on entity change is owned by the evaluator.

## Consequences

### Positive

* The resolved/regressed logic has a correct, total order that does not depend on untrusted agent clocks or
  unsortable version strings.
* Semver still reads nicely where it exists, without ever letting display parsing corrupt ordering.
* Messy telemetry degrades predictably into explicit buckets instead of crashing or silently merging.
* Ingest stays fast because version resolution is cached.

### Negative / accepted costs

* **First-seen-arrival ordering can misorder deliberate out-of-order ingests** (e.g. backfilling an old
  build after a new one). Accepted: it is the only trustworthy monotonic fact in an air-gapped setting, and
  the alternative (trusting agent timestamps/strings) is worse. The sequence reflects when the server first
  learned of a version, which is a defensible definition of order for this product.
* **Synthetic buckets can collect noise** (`unversioned`, `unknown-instance`). Accepted: explicit,
  inspectable buckets are strictly better than fabricated identity or dropped data.

### Neutral / future-facing

* If a site ever needs a different ordering semantics (e.g. honor a trusted CI-supplied sequence on a
  connected, non-air-gapped deployment), that is a provider of ordering, not a change to this default; a
  successor ADR can add it without disturbing the air-gapped default.

## Links

* Version as a first-class axis and the entities these rules resolve: ADR 0007 (TOBS-7).
* Where resolution/upsert/caching runs (the evaluator) and the provision-then-admit path that creates a
  Version: ADR 0004 (TOBS-4).
* Version as partition key, enabling retention-by-build: ADR 0005 (TOBS-5).
* Originating design note: `docs/holdfast-architecture.md`, "Identity / ordering / dedup rules".
