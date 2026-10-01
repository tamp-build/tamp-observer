# Holdfast; Architecture & Design Handoff

Status: design notes at the 5000-foot level. Not a spec; context for building one. Written for a developer (or Claude Code) picking this up cold.

> NOTE (repo transition, decided): Holdfast never reached a stable/usable state. It is being folded into the tamp ecosystem as a new pillar (working name tamp-observer), alongside tamp-build (attestation) and tamp-findings (security/gating); it serves tamp's existing "keep it in-house" thesis, not a specific distributed client. Plan: build fresh in a new tamp-observer repo with clean initial history (no highlight.io-derived lineage, which matters since it joins the attestation ecosystem). The existing github.com/holdfast repo will be edited to point at tamp-observer as the forward replacement for this functionality; once tamp-observer is up and running, holdfast will be archived. The rest of this doc still says "Holdfast" and frames the thing as a product distributed to contractors; both are stale and to be revised (rename to tamp-observer; reframe as an in-house tamp pillar). "Holdfast" may survive as an internal codename.

> NOTE (audience breadth + enforcement mode, decided): The product must serve the full span from a small dev shop running Docker Compose up to a defense/gov/FedRAMP deployment, same as tamp-findings spans advisory-for-one-project up to full attestation suite. This is a POSITIONING and DEFAULTS correction, not a redesign; the architecture is already a dial (storage tier, buffer tier, auth, capture policy are all knobs). Required changes, mostly framing/packaging:
> - Default to the FLOOR, not the ceiling. Out-of-box default = one `docker compose up`, Postgres only, no Valkey, no ClickHouse, simplest auth, sane capture defaults; value in minutes without reading about tiers/air-gap/ITAR.
> - Reframe the lead from "defense/ITAR tool" to "self-hosted in-house observability that scales from a laptop to a locked-down enclave." Air-gap/ITAR is the proof of the ceiling and a credibility signal, not the identity. The universal thesis is keep-it-in-house (solo dev distrusting cloud pricing through fed agency that legally cannot use cloud); air-gapped is just its most extreme expression. The "Hard constraints" section below is really "capabilities available when dialed up," not universal requirements; the floor relaxes most of them (a dev shop with Docker has internet and doesn't care about reachback).
> - Tier the docs to mirror the config tiers: Quick start (Compose, 5 min, errors+replay) -> Growing up (add buffer tier, add ClickHouse, when/why) -> Hardened/air-gapped/regulated (in-enclave auth, RBAC, no-reachback, ITAR posture). A dev shop reads page one and stops; a defense integrator reads all three. Nobody hits "ITAR" before "hello world."
> - INVARIANT (additive, omittable tiers): every higher tier is additive and independently omittable; the floor runs with nothing but .NET + Postgres + the Go collector. A floor install must not drag in a dormant Valkey/ClickHouse. "Small enough for a dev shop with Docker" must be literally true.
>
> ENFORCEMENT MODE (mirror tamp-findings exactly): tamp-findings takes a startup env var that declares locked-down enforcement; set it and gates/compliance are enforced non-negotiably, leave it out and you get advisory mode with the knobs to tone things down. Observer runs the SAME way, with the SAME env var name/value vocabulary/semantics (TODO: look up the exact var name + accepted values in tamp-findings and match to the letter; do not invent a parallel switch).
> - Absence of the var = advisory = the friendly, permissive, fully-configurable dev-shop default. The small shop never sets it and never needs to know the regime exists.
> - Presence (locked) = the non-negotiable posture for defense/gov/FedRAMP.
> - INVARIANT (mode gates, not defaults): in locked mode the loosening PATHS are genuinely ABSENT, not merely pre-set to strict. Code under `ENFORCEMENT=locked` must not consult the override config at all; the gate is checked at the mode boundary, so an assessor can trust the loosening CANNOT happen. "Locked just changes defaults" is the failure mode to avoid (someone could still flip a value and silently exit the regime).
> - The mode governs the ENFORCEMENT surface only: whether identity capture can be enabled in prod, whether session data can transmit without consent, whether PII masking can drop below strict, whether auth can be less than the in-enclave RBAC posture, whether any component gets a reachback path, whether capture policy can relax below a floor, whether audit (when it exists) can be disabled.
> - The mode does NOT govern the SCALE/PERFORMANCE surface: storage tier, buffer tier, ClickHouse-vs-Postgres, how big you scale. Enforcement posture and scale are ORTHOGONAL axes. A small defense subcontractor may run locked-on-Postgres-floor; a funded startup may run advisory-on-ClickHouse. The env var touches only enforcement.

## What Holdfast is

A from-scratch .NET observability platform for self-hosted, keep-it-in-house deployments, spanning a small dev shop on Docker Compose up to an air-gapped / ITAR-restricted defense environment. Began as a port of highlight.io; the port became a ground-up rewrite once it was clear the SaaS-shaped original didn't fit single-tenant, no-cloud deployments. (See the audience-breadth note above: lead with the in-house-at-any-scale framing; the air-gap/ITAR end is the ceiling, not the identity.)

The product does what highlight/Sentry do (error monitoring, session replay, logging, tracing, metrics) but under constraints those tools don't meet.

## Hard constraints (these drive every decision)

- Self-hosted, single-tenant. An installation is the boundary; there is no SaaS account/org/tenant layer. The air-gap is the isolation, not row-level security.
- Air-gapped capable. Runs on an isolated LAN. Data over the wire inside the enclave is fine; reaching out to any cloud service is not.
- No runtime call-home. No component may phone home for telemetry or license activation. This disqualifies anything with a license gate that revalidates against a vendor server. Favor Apache/MIT engines (no license gate = no phone-home vector to audit).
- ITAR / defense data. Not just PII; export-restricted and contractor data that must not leave the customer's infrastructure.
- .NET house. One small team. Minimize languages, toolchains, and accreditation surfaces.
- Security and speed are the two paramount axes.
- Encryption at rest is handled at the storage/volume/cluster layer, not by the application. Don't design around DB-level at-rest encryption.

## Entity model

Spine: Project -> Service -> Environment / Version -> Events, with Area as a side tag.

- Project; the only administered entity. Config, ingestion identity, storage tier, retention policy. A human creates these. Do NOT model a Client/Tenant entity; the installation is the client. (A multi-client single binary, if ever needed, is a future discriminator column, not a layer now.)
- Service; discovered from OTLP `service.name`. Auto-registered on first sight. The application/service grain. Identity = `service.name` scoped within Project (decide whether `service.namespace` folds into identity).
- Environment; discovered from OTLP `deployment.environment`. Auto-created on first sight, but policy-bearing (see Capture policy). QA/dev/test/staging/prod. This is the one discovered entity that carries behavior; it's the scope operators attach capture policy to. Orthogonal to Version.
- Version; discovered from OTLP `service.version`, scoped per-Service. The correctness/partition axis. Auto-created on first sight. Makes "resolved in version N / regressed in N+2" computable. Strong candidate for physical partition key (Postgres partitions, ClickHouse partitioning), so dropping an old version's data is a partition drop, not a mass delete.
- Area; project-scoped metadata tag. Filter/group/report only. No hierarchy, no behavior, no children, no access control. Many-to-many (an event can be several areas). Lean managed-vocabulary (a thin per-project Area lookup referenced by tag) rather than free-text, so reports stay coherent and renames are clean.

Dimensions stamped on events, not structural entities:
- Instance (`service.instance.id`); backend process/host instance, self-reported, synthesized-and-flagged if missing. A slice/triage dimension, not part of identity.
- Session id; frontend session, client-minted opaque UUID. Not a user, not an IP. The key for replay events and for stitching to backend errors.

### Identity / ordering / dedup rules

- Version ordering: `service.version` is free-form (git shas, CI numbers, semver+build). Do NOT sort on the string. Assign a server-controlled monotonic sequence from first-seen arrival time at the collector (the one monotonic fact you own; agent clocks in air-gapped boxes are untrusted). Parse semver opportunistically for display only.
- Missing/empty `service.version`: bucket into a synthetic `unversioned` build per service. Don't crash, don't silently merge.
- Missing `service.instance.id`: bucket into a synthetic `unknown-instance` (don't fabricate distinct instances you can't actually distinguish).
- Dedup = upsert-by-natural-key on Project -> Service -> Version. Cache the resolution; do not do a DB round-trip per event for a Version seen a million times.
- Unknown-project policy: reject to quarantine, do NOT auto-create. Project is the human-granted trust root; Service/Version are discovered *within* a trusted project. Deliberate asymmetry: Service/Version auto-create, Project does not.

## Pipeline

Agents -> Go OTel collector (receive + land raw) -> raw transient bucket -> .NET evaluator (classify/admit) -> tiered store. Rejects -> quarantine/archive.

### Ingestion (Go)

- A custom OpenTelemetry Collector distribution built with `ocb` (OpenTelemetry Collector Builder). Not a from-scratch receiver; extend the real Collector.
- Why Go: cold-start and footprint are solved by default (static binary, millisecond start, no JIT warmup; the autoscale cold-start fear is real only on scale-out and Go removes it), and Go is the native language of OTel/the Collector, so receivers and OTLP handling already exist and are battle-tested.
- The collector's job is only: terminate OTLP (gRPC + HTTP/protobuf), let the standard receiver prove the payload is well-formed OTLP (transport-valid; a byproduct of deserializing), then hand off. No project lookup, no field validation, no entity resolution, no stamping. Dumb, fast, stateless-ish.
- Collector pipeline maps onto our design: receiver -> processor (minimal) -> exporter. Use a thin exporter that lands the raw payload durably and acks. Keep storage/tier logic OUT of Go; it lives in .NET. Go stays a pure fast pipe.
- Build in a Linux container for a Linux target (the pods are Linux). Avoids the Windows Go-build pain entirely (which was Defender/EDR interception and CGO/MinGW, not the language). `CGO_ENABLED=0` for pure-Go.
- Raw format = unparsed/opaque OTLP payload (store the bytes, re-serialized canonical or original-wire; decide; original-wire has a mild forensic nicety for ITAR). Minimal envelope: receipt id, received-at, source, transport metadata. A parse failure becomes an evaluator verdict (-> quarantine), not a landing-stage crash.

### Raw bucket (transient)

- Transient by definition. An event is either consumed (promoted) or moved to quarantine/archive. Nothing lingers in raw. Invariant: every event leaves raw for exactly one of {store, quarantine}.
- It's a flow buffer, not a filing cabinet. Pick for flow, not custody.
- Tiered, same upward-only principle as storage:
  - Low/default tier: in-process bounded queue (`System.Threading.Channels`) or a consumed-and-deleted Postgres "pending" table (deleted on consume, so minimal vacuum churn). No new infra.
  - High tier: Valkey Streams (not Redis; Valkey for the licensing reasons, drop-in BSD fork) as the durable fast landing buffer, drained with consumer-group ack.
- This subsumes the earlier separate "buffering tier"; the raw bucket IS the shock absorber.

### Evaluator (.NET)

- Consumes from raw. This is where all domain intelligence and entity resolution live (the hot collector path stays dumb).
- Treats everything in raw as well-formed-but-untrusted ("it's valid OTLP" says nothing about whether it's data we want).
- Domain adjudication: supported-project check (reject unknown projects to quarantine), required-field validation, Project/Service/Environment/Version resolution and upsert.
- Verdicts: admit (valid+supported -> promote to store) / reject (-> quarantine with reason) / provision-then-admit (valid but needs a side effect first, e.g. auto-create the Version, then admit).

### Quarantine / archive

- Separate, durable, queryable custody store (distinct from raw). Postgres table (or cheap object/file storage for raw bytes). Rejected events sit here with a reason, inspectable; critical for debugging air-gapped sites you can't interrogate live.

## Storage tiering

Tiers are a knob, per Project (consider per-Service too; a site with one chatty app and three quiet ones shouldn't put all four on the heavy tier). Migrations go UP only.

- Baseline / system of record: Postgres + Marten. Quiet at idle, accreditable (on every approved-products list, defense security teams know how to ATO it), license-clean, no reachback, first-class .NET (Npgsql/Marten). Marten gives RavenDB-like freeform-JSON document ergonomics (jsonb + GIN indexing + LINQ + promote-fields-to-columns) plus a built-in event store that suits an event pipeline. This is where the "freeform JSON, fast document queries" feel we liked about RavenDB now lives.
- Top tier (opt-in, paramount performance, infra-costly): ClickHouse. Apache 2.0 (no license gate -> cleanest air-gap story), best columnar speed for large-range aggregations and high-volume ingest.
  - Known cost, by design, not misconfig: ClickHouse's continuous background merge/compaction means real idle CPU even on a quiet, low-volume node (observed ~10% continually). That's the columnar-OLAP tax; the read speed comes from the background work. Mitigation for dev: disable `system.*_log` tables. For the product: it's why ClickHouse is opt-in-for-high-volume, not the default. A small quiet site should never be on it.
- Where Postgres fails vs ClickHouse (so the tier choice is principled): full-scan aggregations over large row counts (p95-over-30-days, group-by across tens-to-hundreds of millions of rows; structural, not an index gap), sustained high-volume ingest (row writes + index maintenance + autovacuum churn), compression/storage footprint at high retention, and high-cardinality group-bys at scale. It does NOT fail at point lookups, recent-slice reads, or the document-y "this error and everything attached" queries. Trigger is data-volume-per-site.
- DuckDB (MIT, embedded, columnar, zero idle) is the on-demand analytical accelerator below ClickHouse scale: Postgres stays the quiet system of record, DuckDB does heavy columnar aggregations on demand (over Parquet exports or straight from Postgres) and goes idle again. ClickHouse only earns its slot when per-site ingest is high enough that you want an always-hot columnar ingest target rather than an on-demand accelerator.

Rejected as storage: RavenDB. Loved it (freeform JSON, fast queries, was ahead of its time) but: document store not columnar (weak on the analytical aggregations that matter), encryption-at-rest was a paid tier (moot now, we do volume-layer), AGPL/commercial server with an OEM licensing + offline-activation question for a distributed air-gapped product, and RQL is the odd-one-out that fights a SQL-shaped provider abstraction hardest. Marten-on-Postgres recovers the ergonomics we actually wanted without the licensing/analytical/abstraction costs. (RavenDB still shines as a whole-app single ACID multi-model platform; just not as one interchangeable provider in a tiered matrix.)

### Migration rules (upward-only ratchet)

- Migrations go up only. Never build the downward path (columnar-back-to-relational reconciliation is the quarter-eating migration we're deliberately deleting).
- Open decision to pin before building: does Postgres stay canonical (ClickHouse is a derived, always-rebuildable read tier; upgrades cheap and reversible) OR does ClickHouse become canonical at cutover (cheaper storage, but the upgrade is truly one-way and you own a backfill)? This changes what "migrate up" means.
- The write model must be a superset both engines accept from day one: design the canonical event schema against the more-constrained target (ClickHouse column types, weaker joins) even while a site is still on Postgres. Then backfill is a copy, not a transform.
- Cutover = stand up the new tier, backfill history, dual-write/parallel-run until caught up and verified, flip reads. The raw-bucket/stream tier makes this painless (point a second consumer at the new store to backfill-and-catch-up).
- The tier is a ratchet in config; make a downgrade illegal in the config model (it implies the reverse migration we didn't build), not just undocumented.

## The storage abstraction (what makes tiers real)

Don't build one entity model with one query path; build one WRITE model and a capability-based READ interface with per-engine translators.

- `IEventSink.Write(batch)` ; identical contract whether called by the synchronous collector path or a stream consumer. Buffering slots in FRONT of the sink, never a rewrite of it. That's what lets the raw-bucket tier be a config flag, not a fork.
- `IObservabilityStore` ; capability-based, not lowest-common-denominator SQL. The four candidate engines (Postgres/Marten, DuckDB, ClickHouse, and historically RavenDB) share no common query language or model; a single SQL string shatters on the interesting aggregations. So define operations as intent, not SQL: `GetLatencyPercentiles(window, groupBy)`, `TopErrorsByFrequency(window)`, `SessionsForError(id)`. Each provider translates intent to its native dialect. Small per-provider translators, not one generic query builder.
- Write side and point/recent-slice reads abstract cleanly; the analytical reads are where it leaks, hence capability-based.
- Pick the capability surface by the weakest intended provider's reach, allow provider-specific extensions. DuckDB and ClickHouse share the most translator code (both columnar SQL), so a DuckDB->ClickHouse scale-up is the smallest dialect jump.
- Decide per-engine whether a tier switch is "new data only, old stays queryable in place" or "full backfill"; it shapes the interface.

## Capture; what we collect

We don't collect a fixed set; we collect whatever a service archetype emits. Archetype = where it runs and what failure looks like there. Organize capture by archetype x runtime/language, NOT by language alone (Sentry organizes by language because its SDKs are deep per-language; we have OTLP, so we lean on OTel's existing SDKs for the language axis and concentrate real effort on the processing that turns OTel-grade exception data into Sentry-grade issues).

Two build categories:
- Telemetry OTel already captures (cheap, broad): traces, spans, metrics, logs across .NET/Java/Python/Node/Go/etc via existing OTel SDKs + auto-instrumentation. For these we mostly document "point your OTel SDK at our endpoint."
- The error/exception experience OTel does NOT give well (valuable, hard): structured/symbolicated stack traces (OTel carries stacktrace as an opaque string), exception grouping/fingerprinting, breadcrumbs, rich context, source maps. This is the processing layer in the .NET evaluator/store and is the real "like Sentry" value; it's language-agnostic because it works on OTLP exception data regardless of source.

Build streams, priority order:
1. .NET capture middleware ; deep, first-class, our flagship SDK and our own dogfood (ASP.NET Core exception middleware/`IExceptionHandler`, structured frames, in-app detection, `ILogger` breadcrumbs, unobserved-task/appdomain catches).
2. Exception processing (grouping/fingerprinting, frame structuring, symbolication, the Issue entity) in the .NET side; the actual differentiator.
3. Broad language coverage via OTel ; documentation + thin shims where a language emits exceptions weakly over OTLP. Not deep per-language SDKs.

Protocol decision: pure-OTLP for everything, push exception richness into attributes and reconstruct structure in the evaluator, to protect the single-protocol boundary. The ONE deliberate exception is the browser/session archetype (below), which gets its own non-OTLP front door. So the collector has exactly two front doors: OTLP for everything, and a session endpoint for replay.

### Archetypes (use cases to document, not yet drill into)

- Browser / JS (session archetype); the session is the primary object. Its own data shape (see Session replay). PII-sensitive, ephemeral, client-side. The one that breaks OTLP-pure.
- Managed long-running server (Kestrel / API / WFE); OTel sweet spot. Request/response lifecycle, traces+spans natural, exceptions on the pipeline, continuous metrics. One web app is often many of these Services, correlated by trace. Deep .NET path.
- Native long-running host (Windows service, desktop app e.g. DasBook); long-lived but no HTTP request to hang a trace on; failure = unhandled/appdomain exception or process death; lifecycle = start/stop/crash. Wants heartbeat/liveness, crash capture, event-log integration, deferred/offline reporting (runs on machines we don't control, maybe offline, reports later).
- Ephemeral / batch / CLI (e.g. the tamp-build ecosystem); processes that are SUPPOSED to exit. No long-lived process, no session, often no network at failure, run may be seconds. Monitoring = a run/invocation record (success? duration? exit code? output? why failed?) flushed synchronously before exit. A build pipeline is a sequence of these, correlated to build/commit. Underserved by every existing tool; natural proving ground via tamp-build. Wants a lightweight synchronous flush-on-exit shim and a report-to-file path for air-gapped build boxes.
- (Mobile later, if ever.)

Archetype defines the capture profile: which signals, the unit-of-work (request / session / run / process-lifetime), the flush model (background vs synchronous-before-exit), the failure model (exception / crash / nonzero-exit / timeout / silence). Runtime+language defines the implementation (.NET-native where deep, OTel+shim where broad).

### Session replay (the browser archetype, in depth)

How highlight did it, and what we inherit:
- Not video; a DOM reconstruction via rrweb (open source, MIT). On session start, serialize the full DOM to JSON (tree + computed styles) so replay reconstructs appearance without fetching real assets. Then a `MutationObserver` records every DOM change as a timestamped diff, plus interaction events (mouse/click/scroll/input/viewport) on their own timeline. Replay = load snapshot into a sandboxed iframe, play the mutation+interaction stream in time order. Structured event log, not frames; much smaller and inspectable.
- Console + network breadcrumbs interleaved on the same timeline.
- Ships in batched chunks over the session's life (not one-per-mutation), continuing for minutes-to-hours. Bursty cadence, unlike a backend exporter.
- Storage split (maps onto our tiering): session METADATA (duration, UA, page, error count, device, searchable attrs) goes in the queryable analytical store; the replay event PAYLOAD is an opaque blob in bulk/blob storage, chunked, fetched by session id on demand. We query the index to find the session worth replaying; we fetch the blob to replay it. Don't put the DOM-mutation firehose in the analytical DB.
- Session id: client-minted opaque UUID at session start. Not a user/IP.
- Error correlation (the money feature): frontend passes its session id on outbound requests (a header); backend errors carry the session id too; lets you jump from a server exception to the exact replay of the user who hit it. Design this cross-archetype key early.
- Privacy at capture: rrweb masking happens client-side at record time, before chunks ship (strict mode obfuscates all text+images, irreversibly, client-side; default mode masks inputs + regex-matched PII; `highlight-mask`/`-block`/`-ignore` classes). Heavy/sensitive signals (network request/response bodies+headers, canvas) are opt-in, off by default. For ITAR: strict-mode-by-default + client-side stripping + allowlist-not-blocklist masking is the posture. (Note the regex default is best-effort and misses names and non-text/canvas PII; strict is the only hard guarantee.)
- Build cost: capture is mostly "integrate rrweb," not invent. Real work = the chunk-stream ingestion path, the metadata/blob split, the session-id correlation to backend errors, and the replay UI.

### Smart, server-triggered, consented capture (our improvement over highlight)

The objection that killed the original highlight proposal: session data stored by a third party in the cloud. Because Holdfast owns the whole pipe (no third party), the server can decide when a session is worth persisting and signal the client in near-real-time; something highlight structurally could not do. This is a genuine differentiator, not a me-too.

- Rolling client buffer (keystone, mandatory): the client always lightly records into a bounded in-memory ring buffer (last N seconds of DOM mutations + breadcrumbs, continuously overwritten, NEVER transmitted). A trigger flips it from buffering-and-discarding to flush-the-buffer-and-stream. This is the only way server-triggered capture catches the lead-up to the error, not just the aftermath. Cheap, memory-only, privacy-friendly (nothing persists/transmits until a reason exists).
- Server-side trigger: the .NET evaluator is already watching the error stream; detecting a per-session anomaly (error-rate spike over a baseline) is a small rule on data we already ingest. On trigger it sets a per-session recording flag.
- Bidirectional client channel (new architecture beyond pure OTLP export; backends don't need this): the JS SDK listens for directives keyed to its session id. Lean PULL (client polls "should I record?" every few seconds) over PUSH (held SSE/websocket): a few seconds of lag is fine because the rolling buffer backfills the lead-up anyway, and pull avoids a persistent connection per active session. The keystone (buffer) is what lets us pick the cheaper channel.
- Consent is a host-app callback, NEVER SDK-rendered UI. We don't know their UI stack (React/Angular/Blazor/jQuery/...), and injecting our DOM into their page breaks layouts and spooks reviewers. The SDK exposes a hook ("recording requested for this session; call approve()/deny()") and waits for the host's verdict before flushing. Host decides how to ask (their modal, their copy) or pre-approves by policy (internal/defense apps where consent is by employment/policy and the prompt is skipped). We give the mechanism, they set the policy.

### Environment capture policy (operator knobs)

Capture policy binds to Environment (optionally overridden per-Service), default scope at Environment. This is why Environment is a first-class, policy-bearing axis. Policy is a config bundle:
- Baseline mode: record-always-max-detail (QA/beta) / record-errors-only / record-nothing-until-triggered (prod default).
- Trigger rules: operator-tunable "outlier spike" definition ("normal" is wildly app-specific). Expose concrete models + numbers: errors-per-minute-per-session over X, rate over Y-times-baseline (z-score/multiplier), absolute count in a window. Declarative, server-side, operator-authored (tune without redeploying the client SDK).
- On-trigger actions: start session recording (flush buffer + stream) and/or alert.
- Detail level: DOM only / DOM+network bodies (opt-in heavy) / console / etc. QA max; prod lean (PII + volume).
- Beta test = a policy preset (record-always-max-detail, optionally time-boxed), not a special feature.
- Promotion is OBSERVED, not performed: the same Version appearing under a new Environment (telemetry arrives with `deployment.environment=prod` instead of `qa`). Each environment applies its own policy. Version is stable across environments (so a QA-caught bug recurring in prod is computable); Environment scopes the policy.
- Implies computing per-Service+Version error-rate baselines; a modest standing workload for the evaluator.

## App feature inventory

The pipeline is plumbing; these are what a user logs in for.

- Issue model (error grouping/fingerprinting); THE core feature. Collapse N occurrences of the same bug into one Issue with count, first/last-seen, affected-version list, status (unresolved/resolved/ignored/regressed). The "resolved in version N / regressed in N+2" state machine is computable thanks to the Version axis. Without this it's a log pile, not an error monitor. Lives in the evaluator/store.
- Symbolication / source maps; turn minified JS (and compiled .NET/native) stack traces back into real source. Upload + storage + de-minify. Without it, stack traces are gibberish.
- Alerting & notifications; alert rules (new issue, spike, threshold, the session-trigger) + delivery. Air-gap-appropriate channels only (email relay, syslog, webhook to internal systems, dashboard badge; NO cloud notifier). Alert on absence/silence too (heartbeat/liveness; the Windows-service and CLI archetypes need this).
- Search / query across sessions, errors, logs; a filter language/UI over the event streams (Area, Environment, Version, attributes feed it). Without good search nobody finds the session worth replaying. The metadata-vs-blob split pays off here.
- Dashboards & charting; user-built graphs, overview screens, error-rate-over-time, p95 latency, throughput. Chart types, time ranges, saved views, per-project dashboards.
- Logging as a first-class product; searchable/filterable log explorer with live tail, correlated to trace/session. A primary daily-use screen for many.
- Distributed tracing UI; the span waterfall/flamegraph, service map, latency breakdown. The APM face. Storing spans != the flamegraph people use.
- The correlation experience; error -> session replay -> logs -> trace. Highlight's thesis and our differentiator. We have the keys (session id stitched to backend errors); this is the UI that walks the graph. Make it an explicit feature, not an emergent property.
- User / segment identification; per-Environment capture-policy field (with per-service override), default OFF. Identity-mode = off / pseudonymous-hash / full. Distinct from the always-present session id so sessions degrade gracefully when identity is off (replay still works, just not linked to a named user). Typically on for DEV/QA, off for prod-with-real-data; depends what the app holds. Default-off is the right posture; turning it on is a deliberate, auditable act.
- Retention & data lifecycle (the admin UI/policy over the storage tiers); "keep prod errors 90 days, QA sessions 7 days, drop old builds." Retention as "keep N builds" aligns with the Version axis and the resolved-in-newer logic.
- Out of scope (for now): feature flags / experimentation (highlight added it; it's what LaunchDarkly bought them for; different product, skip unless demanded).

## Auth / identity / access

- AuthN: always external, no local auth, no local password store ("we don't hold credentials" is a feature here). Ship with GitHub OIDC. Later: definable OIDC provider. NOTE: GitHub OIDC assumes reachability to GitHub, which a truly air-gapped site lacks; so definable/in-enclave OIDC (their AD/Keycloak/etc) is load-bearing for actual air-gapped installs, not an optional nicety. GitHub-only MVP effectively targets connected/dev deployments.
- AuthZ: native Holdfast RBAC, built-in, from the start. Do NOT rely on external IdP roles (e.g. Entra) ; they don't model Holdfast resources and can't be relied on in locked-down tenants. Model roles/permissions around Holdfast resources (Project / Environment + capability verbs: view-replay, edit-policy, manage-users, view-errors, ...). The IdP authenticates and optionally supplies group claims; a mapping layer (later) maps groups -> Holdfast roles as INPUTS, never as the role model itself.
- Audit logging: later. But route every authz check through a single chokepoint seam now (`IAuthorizationService.Check(...)`) so audit becomes "emit from the chokepoint" rather than a retrofit into many call sites. In ITAR land "who viewed which replay, when" is likely a compliance requirement.

## Stack

- Ingestion: Go, custom OTel Collector distribution (`ocb`), built in a Linux container for Linux pods. Thin exporter hands off to the .NET side; storage logic stays out of Go.
- Backend / API: .NET, OpenAPI. Hosts the evaluator, storage providers, RBAC, the API the frontend consumes.
  - Consider Native AOT specifically for any hot-path .NET ingestion-side service (AOT-aware: source-generated protobuf/JSON, no reflection on the hot path). The storage/query side (Marten, reflection-friendly) stays regular JIT. (Note: with the Go collector owning ingestion, the AOT question may be moot; keep it in pocket.)
- Storage: Postgres/Marten baseline -> (raw/buffer tier) -> ClickHouse opt-in top tier. DuckDB as on-demand analytical accelerator below ClickHouse scale. Upward-only migration.
- Frontend: Svelte over the .NET OpenAPI API, typed TS client generated from the OpenAPI spec. Chosen deliberately over Blazor WASM: locked-down/STIG'd environments sometimes block WebAssembly at the browser/policy layer, which would brick a WASM app; plain compiled JS is reliably permitted. Svelte also ships small no-runtime bundles (good for modest air-gapped hardware), is the native home for rrweb and the viz libraries, is a stack the team already runs in production, and gives better mobile-web support.
  - rrweb replay player and heavy viz (flamegraph, virtualized logs) are framework-agnostic JS wrapped as Svelte components; clean, no interop-marshaling awkwardness.
  - Mobile: responsive web triage (issues, error detail, dashboards, alerts, search) is a clean fit. Session replay works on mobile but is best on a larger screen (reconstructing a desktop-sized DOM; thumb-scrubbing a timeline on a 390px viewport is inherently constrained). Mobile = triage + lighter views; replay = larger screen.

## Open decisions / TODOs (deferred, none block starting)

- Per-site realistic volume + retention numbers; sets where the ClickHouse line actually falls (DuckDB-on-demand vs always-hot columnar ingest target).
- Canonical-truth-at-cutover: Postgres stays canonical (ClickHouse derived/rebuildable) vs ClickHouse becomes canonical at cutover (own a backfill). Shapes the migration code.
- Raw payload storage: re-serialized canonical OTLP vs original wire bytes (latter has forensic/provenance nicety for ITAR).
- Service identity: does `service.namespace` fold into Service identity?
- Area: confirm managed-vocabulary over free-text (recommended).
- Alerting delivery mechanics in an air-gap (email relay / syslog / internal webhook / dashboard badge); the most underspecified feature area.
- Identity off-state default: off-entirely (recommended for prod/ITAR) vs pseudonymous-hash; expose all three modes.
- Tier scope: per-Project vs per-Project-and-Service for both storage tier and raw-bucket tier.
- Trace/dashboard/log-explorer UIs are large unbuilt surfaces (named, not designed).
- Replay-player reality check: prototype the rrweb Svelte wrapper early to confirm the scrub-timeline ergonomics before committing deep.
