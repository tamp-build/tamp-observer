# Architecture Decision Records

Decisions about tamp-observer's architecture, conventions, and governance live here. Each
file is a single decision in [MADR](https://adr.github.io/madr/) format, matching the house
style used in [tamp-build/tamp](https://github.com/tamp-build/tamp) and tamp.findings.

tamp-observer is the observability pillar of the tamp ecosystem, alongside tamp (attestation)
and tamp.findings (security/gating). Two contracts are owned upstream and consumed here:

* The **tamp emission / diagnostics contract** (tamp ADR 0018) is owned by tamp-build/tamp.
* The **enforcement-mode vocabulary** (`advisory` / `enforcing` + a `locked` floor) is owned by
  tamp.findings ADR 0004. tamp-observer mirrors it rather than inventing a parallel switch.

## Lifecycle

| Status       | Meaning                                                                                  |
|--------------|------------------------------------------------------------------------------------------|
| `Proposed`   | Drafted, open for discussion. Subject to change before any code depends on it.           |
| `Accepted`   | The decision is current. New code should follow it.                                      |
| `Superseded` | A later ADR overrides this one. Cross-link both ways.                                    |
| `Deprecated` | No longer applies and has no successor (rare; usually superseded instead).               |

ADRs are append-only. Don't edit an Accepted ADR's substance after the fact; write a new one
that supersedes it. Typo fixes and link repairs are fine.

## Index

| #    | Title                                                                                  | Status   | Tracking |
|------|----------------------------------------------------------------------------------------|----------|----------|
| 0001 | [A new in-house observability pillar; clean-history rebuild](0001-new-observability-pillar-clean-rebuild.md) | Proposed | TOBS-1 |
| 0002 | [Enforcement mode mirrors tamp.findings (advisory/enforcing + locked floor)](0002-enforcement-mode-mirrors-tamp-findings.md) | Proposed | TOBS-2 |
| 0003 | [Ingestion via a custom Go OTel Collector distribution (`ocb`)](0003-ingestion-go-otel-collector.md) | Proposed | TOBS-3 |
| 0004 | [Dumb collector, smart .NET evaluator, transient raw bucket between](0004-dumb-collector-smart-evaluator-split.md) | Proposed | TOBS-4 |
| 0005 | [Storage tiering (Postgres/Marten, DuckDB, ClickHouse); upward-only; RavenDB rejected](0005-storage-tiering.md) | Proposed | TOBS-5 |
| 0006 | [One write model, capability-based read interface, per-engine translators](0006-capability-based-storage-abstraction.md) | Proposed | TOBS-6 |
| 0007 | [Entity model: Project trust root; Service/Env/Version discovered; Area as tag](0007-entity-model.md) | Proposed | TOBS-7 |
| 0008 | [Version ordering via server-controlled monotonic sequence](0008-version-ordering-monotonic-sequence.md) | Proposed | TOBS-8 |
| 0009 | [Pure-OTLP ingestion with one session/replay front door](0009-pure-otlp-with-session-front-door.md) | Proposed | TOBS-9 |
| 0010 | [Session replay via rrweb; metadata/blob storage split](0010-session-replay-rrweb.md)   | Proposed | TOBS-10 |
| 0011 | [Smart server-triggered consented capture (buffer, pull, host consent)](0011-smart-server-triggered-consented-capture.md) | Proposed | TOBS-11 |
| 0012 | [Capture policy bound to Environment (operator knobs)](0012-capture-policy-bound-to-environment.md) | Proposed | TOBS-12 |
| 0013 | [External-only authN (OIDC) + native RBAC; audit via chokepoint later](0013-external-authn-native-rbac.md) | Proposed | TOBS-13 |
| 0014 | [Frontend: Svelte over Blazor WASM](0014-frontend-svelte-over-blazor-wasm.md)            | Proposed | TOBS-14 |
| 0015 | [Issue model (error grouping, fingerprinting, resolved/regressed)](0015-issue-model-error-grouping.md) | Proposed | TOBS-16 |
| 0016 | [Alerting on new/regressed Issues + pluggable channels](0016-alerting-and-notification-channels.md) | Proposed | TOBS-17 |

ADR numbers are stable and gap-allowed: they correspond 1:1 with the YouTrack tracking issues
(`TOBS-N`), so a deferred ADR keeps its slot until written. `Planned` rows are reserved slots with
a ticket but no ADR file yet.

## Authoring a new ADR

1. Pick the next number from the YouTrack ADR list.
2. Copy an existing file as a starting shape; keep MADR section headings.
3. Open as `Proposed` until accepted; flip to `Accepted` and merge.
4. Update this index.
