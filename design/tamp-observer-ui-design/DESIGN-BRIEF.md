# tamp-observer - UI Design Brief

A complete description of what the tamp-observer web UI must do, for design work. Self-contained: you do not
need the codebase to use this. Where something already exists in code it is marked **[built]**; everything else
is **[planned]** and open to design.

---

## 1. What the product is

tamp-observer is a **self-hosted, keep-it-in-house observability platform** - errors, session replay, logging,
tracing, and metrics - the third pillar of the "tamp" ecosystem (alongside attestation and security-gating).

The defining constraint is **range**: the same product must serve a solo dev running one `docker compose up` on
a laptop, *and* an air-gapped / ITAR-restricted defense enclave, with everything in between. Nothing calls home;
there is no cloud dependency. "Keep it in-house" is the thesis; air-gapped is just its most extreme expression.

Think Sentry + Highlight + a tracing/metrics UI, but single-tenant, self-hosted, and dialable from "friendly
dev default" up to "locked-down, audited, no-reachback."

**Design consequences of that range:**
- **Plain compiled JS, no WebAssembly** - STIG'd/locked browsers can block WASM. Small, no-runtime bundles.
- **Works offline / air-gapped** - no external fonts, analytics, CDNs, or call-home. Everything self-contained.
- **Mobile is triage, desktop is depth** - issues, alerts, dashboards, search should be clean on a phone;
  session replay is a larger-screen experience (reconstructing a desktop DOM, scrubbing a timeline).
- **Dial, don't fork** - the UI surfaces the same features whether tiny or hardened; higher capabilities simply
  appear/enable based on configuration and the viewer's role.

---

## 2. Stack

**Frontend [built]**
- **Svelte 5** (runes) + **Vite** + **TypeScript**, built to static assets served by the backend.
- **Typed API client generated from the backend's OpenAPI document** (`openapi-typescript` + `openapi-fetch`),
  so the UI cannot drift from the API without a type error.
- **oidc-client-ts** for the browser OIDC login (authorization code + PKCE).
- **rrweb** (record) + **rrweb-player** (playback) for session replay, wrapped as Svelte components.
- **Runtime config**: the SPA fetches `/config.json` at startup for its OIDC settings, so one build runs in any
  deployment. No per-deploy rebuild.
- Current theme is a **dark, dense developer-tool aesthetic**; the brand/visual system is open to design.

**Backend (context, not something you design)**
- **.NET 10 minimal API** exposing an **OpenAPI** contract (the UI's only data source).
- **Go OpenTelemetry collector** ingests OTLP; a .NET evaluator promotes data into storage.
- **Tiered storage**: Postgres (floor) + in-process DuckDB accelerator + optional ClickHouse (analytical);
  a Valkey raw-bucket buffer. These are deploy-time dials, mostly invisible to the UI (a health/settings view
  may surface which tiers are active).
- **Auth**: external OIDC only (no local passwords). Ships with **GitHub via a Dex broker**; any OIDC IdP works.

---

## 3. Access model (auth, admission, RBAC, enforcement)

This is central to the UI and must be designed carefully.

### 3.1 Sign-in [built]
- Authentication is **always external OIDC**; the app never stores passwords.
- **The landing experience is sign-in**: an unauthenticated visitor is sent straight to the IdP (GitHub), not a
  marketing or empty shell. After auth they return signed in.
- On the bundled dev identity provider only, a visible **"Development / demo mode" banner** is shown (never in a
  real deployment).

### 3.2 Admission allowlist [built]
- **No self-service accounts.** A valid GitHub login is necessary but not sufficient: the user's **email must be
  pre-registered** by an admin, or every `/api` call returns 403.
- UX implication: a signed-in-but-not-admitted user needs a clear, friendly **"you're authenticated but not
  authorized for this instance - ask an admin to add <email>"** state, not a raw 403.

### 3.3 Roles & capabilities [built: model; planned: UI]
- **Roles**: `Viewer`, `Editor`, `Admin`.
- **Capability verbs**: `ViewErrors`, `ViewTraces`, `ViewLogs`, `ViewReplay`, `EditCapturePolicy`,
  `ManageUsers`, `AdministerInstance`.
  - Viewer = all the View* verbs. Editor = Viewer + EditCapturePolicy. Admin = everything.
- **Resource scopes**: a grant applies at `Instance` (everything), `Project` (that project + its environments),
  or `Environment` (one environment). The UI must reflect scoped access - a user may see project A but not B,
  or may view-only in prod while editing in QA.
- UX implication: **navigation and actions are role- and scope-aware**. Hide/disable what the viewer can't do;
  never show an action that will 403. Session replay in particular is a sensitive capability (`ViewReplay`).

### 3.4 Enforcement mode [built: model; planned: UI]
Mirrors the security-gating pillar. An instance (and optionally a project) runs in one of:
- **advisory** - the friendly, permissive, fully-configurable default (the dev-shop default).
- **enforcing** - compliance controls are enforced; certain "loosenings" are refused.
- **locked** - a non-weakenable floor; a project may raise strictness, never lower it.

Under enforcing/locked, specific **loosenings are simply unavailable** (not just defaulted off), e.g.: enabling
identity capture in production, transmitting a session without consent, sub-strict PII masking, weakening RBAC,
component "reachback" (outbound alert channels), sub-floor capture policy, disabling audit. The UI must **show
the current mode prominently** and, where a control is forbidden by mode, show it as **locked with the reason**
("disabled under enforcing mode"), not hidden silently - an operator needs to understand why.

---

## 4. Information architecture

Two scopes of navigation:

**A. Instance-level** (chrome around everything)
- **Project switcher** - pick which project you're looking at. Lists the projects the viewer can access.
- **Global search / command palette** [planned] - jump to a project, issue, trace id, or session id.
- **User menu** - who you're signed in as, role, sign out.
- **Admin area** (Admin only): Users & roles, Notification channels, Enforcement, Instance settings, Storage/
  health.

**B. Project-level** (once a project is selected) - the main product surfaces:
1. **Overview** - the project's health at a glance.
2. **Issues** (errors).
3. **Traces**.
4. **Logs**.
5. **Metrics**.
6. **Session Replay**.
7. **Alerts**.
8. **Project settings** - services, environments, versions, areas, capture policy, symbols.

A persistent **time-window picker** and **environment filter** apply across most project surfaces.

---

## 5. Core entities the UI surfaces

- **Project** - the trust root; data is scoped to it. Has a human-granted key (the ingestion identity).
- **Service** - discovered from telemetry (`service.name`), scoped to a project.
- **Environment** - discovered (`deployment.environment`: dev/qa/staging/prod). **Policy-bearing**: capture
  policy attaches here.
- **Version** - discovered (`service.version`), ordered by a server-assigned monotonic sequence. Enables
  "resolved in version N / regressed in N+2."
- **Area** - a lightweight per-project tag for filtering/grouping (managed vocabulary, no hierarchy).
- **Issue** - a grouped error (see §6.2).
- **Replay session** - a recorded browser session (see §6.6).
- **Allowed identity / role assignment** - the access model (§3).

---

## 6. The surfaces in detail

### 6.1 Overview dashboard [planned]
First thing seen after choosing a project. At-a-glance health:
- Error/issue rate over the window; new vs regressed issues; top issues.
- Latency percentiles (p50/p95/p99) and top operations (with error counts) **[API built]**.
- Throughput, recent deploys (versions), environment breakdown.
- Recent sessions worth replaying; recent alerts.
- Everything is a jumping-off point into the detailed surfaces. Honor the time-window + environment filter.

### 6.2 Issues (errors) [planned UI; model built]
The headline surface. Errors are **grouped into Issues by a fingerprint** (per project + service), so thousands
of occurrences collapse into one row.
- **Issue list**: title, error type, count, first/last seen, affected versions, status, environment, area tags.
  Filter by status/environment/area/version/service; sort by recency/frequency; search.
- **Status lifecycle**: `Unresolved` → `Resolved` → **`Regressed`** (auto-flips when a resolved issue recurs in
  a later version). Design the status transitions and the "regressed" emphasis - it's a key differentiator.
- **Issue detail**: occurrence timeline, affected versions/environments, a **symbolicated stack trace**
  (original source frames, not minified - see §6.8), breadcrumbs, and the **correlation walk**: jump from this
  error to the exact **trace** and the exact **session replay** of the user who hit it (§7).
- Actions (role-gated): resolve / reopen, assign, tag with an area, mute, link.

### 6.3 Traces [planned UI; partial API]
- **Trace list** by service/operation over the window; latency + error filters.
- **Trace detail**: a **waterfall / flamegraph** of spans (parent/child, durations, status), span attributes,
  and interleaved **logs** sharing the trace id **[API built: fetch spans+logs for a trace]**.
- **Metrics views** (p50/p95/p99, top operations) feed off the same data **[API built]**.
- Correlate a span/trace to its logs and, for a browser-originated request, to the session replay (§7).

### 6.4 Logs [planned]
- **Log explorer**: filter by severity, service, environment, version, time; full-text search on the body;
  structured attribute filters.
- **Live tail** [planned] - stream new logs as they arrive.
- A log line links to its trace (and thus the correlation walk).

### 6.5 Metrics [planned]
- Latency percentiles and operation stats exist in the API **[built]**; the UI should present them as charts
  and let the user build simple dashboards (pick service/operation/env, choose a metric, a window).
- Keep charts accessible, legible in dark mode, and printable/exportable for reports.

### 6.6 Session Replay [planned UI; capture + storage + player built]
The browser archetype, and a flagship feature.
- **Session list**: metadata only (duration, UA/device, start URL, event count, error count, user if identity
  is enabled) - this is the "which session is worth replaying?" index. Search/filter by those fields, by
  error presence, by environment. **[API built: list + per-session events]**
- **Replay player**: the **rrweb player** reconstructs the DOM in a sandboxed iframe and plays the mutation +
  interaction stream on a **scrub timeline** - not video. Interleave **console and network breadcrumbs** on the
  same timeline. Design the scrub ergonomics, the timeline, breadcrumb markers, speed controls, and the
  error-moment markers. **[built: basic player wrapper; needs real design]**
- **Privacy is visible**: masking happens client-side at capture. Strict mode irreversibly obfuscates
  text/images; default mode masks inputs + regex PII. The UI should indicate the masking level of a session and
  never imply more fidelity than was captured. Under locked/enforcing, strict masking is mandatory.
- **Error→replay correlation** is the money feature: from a server error, open the exact user session at the
  moment it happened (§7).

### 6.7 Alerts [planned UI; core built]
- **Alert rules**: today the system raises alerts on **new issues** and **regressed issues** **[built]**.
  Planned rule types: outlier **spike** (errors/min over baseline, z-score/multiplier, absolute count in a
  window), **threshold**, **heartbeat** (absence of expected telemetry). Rules are operator-authored,
  server-side, per project/environment.
- **Alert history / feed**: what fired, when, which issue/service/version, which channels were notified,
  acknowledge state.
- Design the rule builder (condition + scope + channels + severity) and the alert feed.

### 6.8 Notification channels [planned UI; channels built]
- Supported channels: **SMTP (email)**, **Telegram**, **Slack** **[built]**; the operator enables one or more.
- **Reachback gating**: Telegram and Slack are outbound/"reachback" channels. Under enforcing/locked mode in a
  no-reachback posture, they are **refused** - the UI must show them as unavailable *with the reason*, while
  SMTP (which can be an in-enclave relay) remains allowed. This mirrors §3.4.
- Config UX: add/enable a channel, enter its settings (webhook URL, bot token + chat id, SMTP host/from/to),
  **test** it, and route which alert kinds go to which channels.

### 6.9 Symbolication [planned UI; JS source maps built]
- Errors arrive with minified stack frames; **symbol artifacts** (JS source maps today; .NET PDB/native later)
  are stored **per project/service/version** and used to resolve original source frames in issue detail (§6.2).
- UX: a per-version **symbols** area to upload/manage artifacts and see coverage ("version 1.4.2 has source maps
  for app.min.js"), plus a clear "symbolicated / not symbolicated" indicator on stack traces.

### 6.10 Capture policy [planned; model forthcoming]
- **Bound to Environment** (the policy-bearing entity): operators tune, per environment, what gets captured -
  session recording triggers (rolling buffer + server-side trigger + consent), **detail level** (DOM only /
  DOM+network bodies / console), and **identity mode** (off / pseudonymous-hash / full; **default off**).
- Presets (e.g., "beta test = record-always-max-detail, time-boxed"). Prod defaults lean (PII + volume).
- Enforcement ties in: identity-in-production and sub-floor policy are forbidden under locked.

### 6.11 Users & RBAC admin [planned UI; model built]
- **Admission list**: add/remove pre-registered emails, assign role + scope. This is the "no randos" gate.
- **Role assignments**: grant Viewer/Editor/Admin at Instance/Project/Environment scope.
- Show effective access per user. Admin-only (`ManageUsers`).
- (Audit is planned: "who viewed which replay, when" - a future audit log surface.)

### 6.12 Instance settings & health [planned]
- Enforcement mode display/controls (§3.4), identity defaults, retention, and a **storage/health** view showing
  which tiers are active (Postgres / DuckDB / ClickHouse / Valkey), ingest health, and pod/queue status.

---

## 7. Cross-cutting: the correlation walk

The single most important interaction thread, designed in from the start via a **client-minted session id** that
rides on requests and is stamped on backend errors:

**server error (Issue) → the exact trace → the logs on that trace → the user's session replay at that moment.**

Design this as a first-class, fluid path (deep links, "open in replay," "see the trace," back-and-forth), not a
set of disconnected tables. It's the differentiator over single-signal tools.

Supporting cross-cutting UX: a global **time-window picker**, **environment** and **version** filters, **area**
tags, and consistent empty/loading/error/permission-denied states on every surface.

---

## 8. Current API surface (what the UI can call today)

Built endpoints (OpenAPI, OIDC + allowlist gated unless noted):
- `GET /config.json` - runtime OIDC config (anonymous).
- `GET /health` - liveness (anonymous).
- `GET /api/me` - the authenticated subject.
- `GET /api/projects/{projectId}/latency` - p50/p95/p99 over a window.
- `GET /api/projects/{projectId}/operations` - top operations with error counts.
- `GET /api/projects/{projectId}/traces/{traceId}` - spans + logs for a trace.
- `GET /api/projects/{projectId}/sessions` - replay session list (metadata).
- `GET /api/projects/{projectId}/sessions/{sessionId}/events` - rrweb events for playback.
- `POST /ingest/replay` - session front door (project-key auth; not a UI call).

Known near-term API gaps the UI needs: **list projects** (`GET /api/projects`), **list/detail issues**, **list
logs**, **alert rules + history**, **notification channel config**, **users/roles admin**, **capture policy**,
**symbol upload**. These are expected additions, not constraints.

---

## 9. Design priorities / asks

1. A **navigation + IA** that scales from one project to many, and from Viewer to Admin, hiding what a viewer
   can't do.
2. The **Issues surface** and the **correlation walk** (§6.2, §7) - the product's center of gravity.
3. The **session replay player** scrub/timeline/breadcrumb experience (§6.6).
4. A coherent **dark, dense, no-runtime, offline-capable** visual system (no external fonts/assets), legible on
   mobile for triage.
5. **Trust/compliance made legible**: enforcement mode, masking level, reachback gating, and permission states
   should be visible and explained, never silent.

---

*Grounded in the project's architecture decision records (ADRs 0001-0018) and the live implementation as of the
first production deploy. "[built]" items are working today; "[planned]" items are the design scope.*
