# ADR 0007: Entity model (Project as trust root; Service/Environment/Version discovered; Area as tag)

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-7

## Context and Problem Statement

Everything the evaluator admits (ADR 0004) has to resolve to a stable set of entities so that errors can
be grouped, versions compared, and policy applied. We need to decide what those entities are, which are
created by a human and which are discovered from telemetry, and which carry behavior versus which are just
tags. Getting this wrong is expensive: it is the schema the store (ADR 0005), the capture policy (ADR
0012), and the Issue model all hang off.

A key constraint from ADR 0001: tamp-observer is single-tenant, the installation is the boundary, and
there is no SaaS account/org/tenant layer. So the entity model must not smuggle a tenancy layer back in.

This ADR fixes the entity spine, the discovered-vs-administered asymmetry, and the dimensions that are
stamped on events rather than modelled as entities.

## Decision Drivers

* **Single-tenant: the installation is the client.** No Client/Tenant entity. The air-gap is the
  isolation, not row-level security (ADR 0001).
* **Humans grant trust; telemetry does not.** Anything that represents a trust decision must be
  human-created. Anything that is just an observed fact of the telemetry should auto-register, so
  operators are not doing data entry to see their own services.
* **Version must be a first-class axis.** "Resolved in version N, regressed in N+2" is the headline of the
  Issue model and is only computable if Version is structural, not a string on an event.
* **Policy needs exactly one home.** Capture policy has to bind to a single, well-defined scope, or it
  becomes ambiguous which policy applies.
* **Reporting needs a lightweight grouping tag** that carries no behavior and no access control, kept
  coherent across reports.

## Decision

### 1. The spine: Project -> Service -> Environment / Version -> Events, with Area as a side tag

* **Project**: the only administered entity. Holds config, ingestion identity, storage tier, retention
  policy. A human creates it. It is the trust root. There is deliberately **no Client/Tenant entity**; the
  installation is the client. (A multi-client single binary, if ever needed, is a future discriminator
  column, not a layer now, per ADR 0001.)
* **Service**: discovered from OTLP `service.name`, auto-registered on first sight. The application/service
  grain. Identity is `service.name` scoped within Project.
* **Environment**: discovered from OTLP `deployment.environment`, auto-created on first sight, but
  **policy-bearing**. QA/dev/test/staging/prod. This is the one discovered entity that carries behavior:
  it is the scope operators attach capture policy to (ADR 0012). Orthogonal to Version.
* **Version**: discovered from OTLP `service.version`, scoped per-Service, auto-created on first sight. The
  correctness/partition axis that makes "resolved in version N / regressed in N+2" computable. Strong
  candidate for a physical partition key (Postgres partitions, ClickHouse partitioning), so dropping an
  old version's data is a partition drop, not a mass delete.
* **Area**: project-scoped metadata tag. Filter/group/report only. No hierarchy, no behavior, no children,
  no access control. Many-to-many (an event can be in several areas). A lean managed vocabulary (a thin
  per-project Area lookup referenced by tag) rather than free text, so reports stay coherent and renames
  are clean.

### 2. The administered-vs-discovered asymmetry

Project is human-granted and is **never auto-created**: an unknown project is rejected to quarantine (ADR
0004), not provisioned. Service, Environment, and Version are discovered **within** a trusted Project and
auto-create on first sight. This asymmetry is deliberate and is the whole trust model: trust is granted to
a Project by a human, and discovery happens only inside that granted trust.

### 3. Dimensions stamped on events, not entities

* **Instance** (`service.instance.id`): backend process/host instance, self-reported. A slice/triage
  dimension, not part of identity. Synthesized-and-flagged if missing (see ADR 0008).
* **Session id**: frontend session, client-minted opaque UUID. Not a user, not an IP. The key for replay
  events and for stitching to backend errors (ADR 0010, ADR 0011).

### 4. Open sub-decision: does `service.namespace` fold into Service identity?

Service identity is `service.name` within Project. Whether `service.namespace` participates in that
identity is left open here (it changes how two same-named services in different namespaces are
distinguished). It does not block the spine; flagged for a follow-up call. Leaning toward folding
`service.namespace` in when present, so namespaced deployments do not collide, with a synthetic default
when absent (consistent with ADR 0008's bucket-don't-crash posture).

## Consequences

### Positive

* No tenancy layer leaks in: single-tenant stays single-tenant, and the trust boundary is the Project.
* Operators see their services, environments, and versions without data entry; discovery does the work.
* Version-as-structure makes the Issue model's resolved/regressed logic computable and makes retention
  "keep N builds" a partition drop.
* Capture policy has exactly one home (Environment), so "which policy applies" is never ambiguous.

### Negative / accepted costs

* **Auto-creation needs guardrails.** Discovery inside a trusted Project means a misconfigured agent can
  spawn junk Services/Versions. Accepted and bounded: it is scoped to a trusted Project, and the
  synthetic-bucket rules (ADR 0008) keep junk from fragmenting identity.
* **Area as managed vocabulary is slightly more friction** than free text (someone curates the list).
  Accepted: free text makes reports incoherent and renames dirty, which defeats the point of Area.

### Neutral / future-facing

* Area managed-vocabulary-over-free-text is the recommended resolution of the doc's open question and is
  adopted here; if it ever proves too rigid, a successor ADR revisits it.
* The `service.namespace` identity question (§4) is the one genuinely open piece and is tracked on this
  ticket.

## Links

* Trust-root enforcement (unknown Project to quarantine) and discovery via provision-then-admit: ADR 0004
  (TOBS-4).
* Identity/ordering/dedup rules for the discovered entities: ADR 0008 (TOBS-8).
* Version as partition key and retention-by-build: ADR 0005 (TOBS-5).
* Environment as the capture-policy scope: ADR 0012 (TOBS-12, planned).
* Session id as the cross-archetype correlation key: ADR 0010, ADR 0011 (planned).
* Single-tenant, no-tenancy constraint: ADR 0001.
* Originating design note: `docs/holdfast-architecture.md`, "Entity model".
