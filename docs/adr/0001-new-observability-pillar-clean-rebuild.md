# ADR 0001: A new in-house observability pillar; clean-history rebuild

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-1

## Context and Problem Statement

The tamp ecosystem has two pillars shipping today: **tamp** (attestation / build) and
**tamp.findings** (security and gating). There is a recurring third need those two don't cover:
*what actually happened at runtime*, i.e. errors, session replay, logging, tracing, metrics. The same
customers who want in-house attestation and in-house gating want in-house **observability**, under
the same thesis: keep it in-house, no cloud, from a solo dev who distrusts SaaS pricing up to a
federal agency that legally cannot use cloud at all.

A prior project, **Holdfast**, aimed at exactly this. It began as a port of highlight.io and became
a ground-up rewrite once it was clear the SaaS-shaped original didn't fit single-tenant, no-cloud
deployments. Holdfast never reached a stable or usable state. Its design thinking, however, is sound
and is captured in [`../holdfast-architecture.md`](../holdfast-architecture.md).

The question this ADR answers: **how does this capability enter the tamp ecosystem (as a
continuation of the Holdfast repo, or as a fresh pillar), and what is its identity and scope?**

## Decision Drivers

* **Clean lineage for an attestation ecosystem.** tamp's entire reason to exist is provenance. A
  pillar whose git history is derived from a third-party SaaS codebase (highlight.io) is a lineage
  liability the moment it sits next to attestation tooling. The history itself is evidence here.
* **Holdfast carries no usable momentum.** It never reached a working state, so there is no running
  system, no users, and no data to preserve. The only asset worth carrying forward is the *design*,
  not the code or the history.
* **Ecosystem coherence.** A third pillar should look, name, govern, and ship like the other two
  (naming, ADRs, enforcement vocabulary, emission contract), not like an imported outsider.
* **Positioning must span the full audience.** Like tamp.findings (advisory-for-one-project up to a
  full attestation suite), this pillar must serve a small dev shop on Docker Compose *and* a
  defense/gov/FedRAMP enclave, from the same codebase, as a dial.
* **Don't strand the old repo.** The existing `holdfast` repo must point forward to its replacement
  so anyone who lands there is routed correctly, then be retired cleanly.

## Decision Outcome

**Chosen: build a fresh `tamp-observer` repo as a new ecosystem pillar, with clean initial history
and no highlight.io-derived lineage. Carry forward the Holdfast *design*, not its code.**

### Identity

* **Name:** `tamp-observer`, in the `tamp-build` GitHub org, peer to `tamp` and `tamp.findings`.
* **"Holdfast"** may survive as an internal codename; it is not the product name and not the repo
  name. All outward framing is tamp-observer.
* **What it is:** a from-scratch .NET self-hosted observability platform (error monitoring, session
  replay, logging, tracing, metrics) for single-tenant, keep-it-in-house deployments. It does what
  highlight.io / Sentry do, under constraints those tools don't meet (air-gap, no call-home, ITAR).

### Clean-history rebuild

* The repo starts with a clean initial commit. No highlight.io-derived commit history is imported.
* The design handoff doc is carried in as `docs/holdfast-architecture.md` (reference material to be
  revised toward tamp-observer framing over subsequent ADRs); the implementation is written fresh.

### Positioning: default to the floor, dial up to the ceiling

* **Out-of-box default is the floor:** one `docker compose up`, Postgres only (no Valkey, no
  ClickHouse), simplest auth, sane capture defaults. Value in minutes without reading about tiers,
  air-gap, or ITAR.
* **The ceiling** (air-gapped / ITAR / FedRAMP) is the proof of how far it scales and a credibility
  signal, *not* the identity. The lead framing is "self-hosted in-house observability that scales
  from a laptop to a locked-down enclave."
* **INVARIANT, additive and omittable tiers:** every higher tier (buffer tier, ClickHouse, in-enclave
  auth, …) is additive and independently omittable. The floor runs on nothing but .NET + Postgres +
  the Go collector. A floor install must not drag in a dormant Valkey/ClickHouse. "Small enough for a
  dev shop with Docker" must be literally true.

### Old-repo transition

* The existing `github.com/.../holdfast` repo is edited to point at tamp-observer as the forward
  replacement for this functionality.
* Once tamp-observer is up and running, the holdfast repo is **archived** (not deleted, since the
  pointer must survive).

### Scope note

This ADR establishes the pillar, its identity, its clean-history posture, and its floor-to-ceiling
positioning. It deliberately does **not** decide the internal architecture: ingestion, storage
tiering, the entity model, capture, auth, enforcement-mode wiring, and the frontend each get their
own ADR. The architecture doc is the input to those; this ADR is the frame they hang on.

## Consequences

### Positive

* **The lineage is clean by construction.** An assessor auditing the attestation ecosystem finds no
  third-party SaaS history in the pillar that watches runtime.
* **The pillar is a first-class ecosystem citizen** from commit one: same org, same ADR discipline,
  same enforcement vocabulary, same emission contract.
* **No migration debt.** Because Holdfast had no users or data, there is nothing to migrate; the
  fresh start costs only re-typing code whose design is already settled.
* **A friendly front door.** Floor-first defaults mean the broadest possible audience reaches "hello
  world" before ever meeting the word "ITAR."

### Negative / accepted costs

* **Re-implementation from the design, not the code.** We forgo whatever partial Holdfast code
  existed and rebuild from the handoff doc. Accepted: that code never worked, and the history is the
  liability we are specifically shedding.
* **The architecture doc is stale on names and framing.** It still says "Holdfast" and frames the
  product as something distributed to contractors. Those are revised progressively via the per-topic
  ADRs; until then the doc carries a correction note at its head.

### Neutral / future-facing

* The tamp-observer YouTrack project (`TOBS`) exists and mirrors tamp.findings' field setup; ADR
  numbers map 1:1 to `TOBS-N` issues per the house convention (this ADR = TOBS-1).
* A multi-client single binary is explicitly *not* modelled now; if ever needed it is a future
  discriminator column, not a tenancy layer. Out of scope here, noted so later ADRs don't relitigate.

## Links

* Design handoff this pillar is built from: [`docs/holdfast-architecture.md`](../holdfast-architecture.md).
* Enforcement-mode vocabulary this pillar mirrors: tamp.findings ADR 0004 (gate enforcement modes and
  the locked floor). To be wired into tamp-observer in a dedicated ADR.
* Emission / diagnostics contract owned upstream: tamp ADR 0018.
* Sibling pillars: tamp (attestation), tamp.findings (security / gating).
