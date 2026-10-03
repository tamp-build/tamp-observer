# tamp-observer UI: design handoff

Hi-fi mockups for the tamp-observer web UI, packaged for implementation in the existing **Svelte 5 (runes) + Vite + TypeScript** frontend. Read this file first, then `DESIGN-BRIEF.md` (the product requirements these mockups answer).

All data in the mockups is **sample data** (project "commonhall", issue ISS-1042, trace 4bf92f35..., session s_7Hq2M). None of it is real. Replace with API data.

---

## 1. What's in this folder

| Path | What it is | Use it for |
|---|---|---|
| `README.md` | This handoff | The spec. Wins over anything implied by the mockups. |
| `DESIGN-BRIEF.md` | Product brief | Requirements, access model, API surface. |
| `tokens.css` | Design tokens as CSS custom properties | Drop into `src/lib/styles/`; build every component on these. |
| `static/*.html` | Plain HTML renders of each screen (open `static/index.html`) | Pixel reference. Open in a browser; inspect with devtools. No JS. |
| `screenshots/*.png` | Full-page captures of each screen at 1440 px (and 390 px for mobile) | Quick visual reference. |
| `source/*.dc.html` | Original design-canvas sources | Data shapes for lists (see the `renderVals()` block at the bottom of each). Not runnable outside the canvas; ignore `support.js`, `<x-dc>`, `<sc-for>`, `{{holes}}`. |
| `source/canvas.json` | Canvas layout index | Ignore unless you need board sizes. |

The static HTML uses a small set of shared CSS classes (`.btn`, `.pill`, `.tag`, `.panel`, `.nav`, `.seg`, `.field`, etc.) defined in each page's `<style>`. They're identical across pages; treat them as the component CSS to port.

---

## 2. Hard constraints (do not break)

1. **Offline / air-gapped.** No web fonts, no CDN, no analytics, no external images. System font stacks only (see `tokens.css`). Icons are inline stroke SVGs; keep them inline or bundle them as Svelte components.
2. **No WebAssembly.** Locked/STIG'd browsers block it. Plain compiled JS only.
3. **Runtime config.** One build serves every deployment; OIDC settings come from `GET /config.json` at startup. Don't bake environment into the build.
4. **Typed API only.** All data via the generated `openapi-fetch` client. Endpoints that don't exist yet: build the UI against a typed mock/adapter with the shape you'd expect, and note it.
5. **Never show an action that will 403.** Navigation and actions are role- and scope-aware (see section 5).
6. **Never silently hide a policy restriction.** If enforcement mode forbids something, show it **disabled with the reason**.
7. **Never imply more replay fidelity than was captured.** Masking level is always visible on a session.

---

## 3. Visual system

Dark, dense developer tool. Full values in `tokens.css`.

- **Accent (cyan `#6FD3E0`)** = interactive: links, primary buttons, selected tab underline, replay playhead. Nothing else.
- **Orange (`#FFAD60`)** = **Regressed**, and the "error moment" in correlation views. Reserved; the product's key differentiator should be the loudest color on screen.
- **Blue (`#93BDFF`)** = Unresolved. **Green (`#80D8B0`)** = Resolved / symbolicated / healthy.
- **Violet (`#D2B8FF`)** = enforcement/policy (mode badge, locked controls, "why" boxes). Never used for status.
- **Red (`#FF9A9A`)** = failed request / span error / log ERROR.
- **Yellow** = warnings, and the dev/demo banner only.
- Type: UI sans 13 px (14 px mobile), mono 12 px for ids, paths, code, versions, timestamps. Section labels are 11 px uppercase, `letter-spacing: .08em`, weight 600, muted.
- Controls 32 px tall on desktop; **44 px minimum on touch**. Radii: controls 6, panels 8, pills full.
- Disabled = muted text + **dashed border**. Locked-by-policy = disabled + lock icon + violet reason text nearby.
- Contrast: text >= 4.5:1. `--muted` is the lowest-contrast text color allowed for real content.
- Layout uses flex/grid with `gap`, not margins. Wide tables scroll inside an `overflow-x: auto` box with a `min-width`; the page itself never scrolls sideways.

---

## 4. App shell

Every project screen (`Main`, `IssueDetail`, `Trace`, `Replay`, `Channels`) shares one shell.

**Top bar (instance level, left to right):** logo `tamp/observer` (links to project home) · **project switcher** (lists only projects the viewer can access) · **command palette** field (`Ctrl K`; jumps to project, issue id, trace id, session id) · spacer · **enforcement mode badge** (always visible; click opens an explainer of what the current mode restricts) · **user menu** (name, role, sign out).

**Sidebar (project level):** Overview, Issues (count), Traces, Logs, Metrics, Session replay, Alerts (count of unacknowledged, orange), Project settings. Then an **Admin** group: Users & roles, Notification channels, Enforcement, Storage & health. The Admin group renders only for users with `ManageUsers`/`AdministerInstance`.

**Filter bar (per surface, top right of content):** environment segmented control (prod / staging / qa / dev; only environments the viewer can see), time window picker, version picker. These are **global state** shared across project surfaces and must round-trip through the URL query string.

**Responsive:** at narrow widths the mockup's sidebar simply wraps above the content. In implementation, collapse it into a drawer behind a menu button below ~900 px. Mobile is **triage**: see `Mobile.html` for the intended phone layout (bottom tab bar: Issues, Alerts, Overview, Search).

**Dev/demo banner:** shown only when signed in through the bundled dev IdP (see `Access.html`, top strip). Never in a real deployment. Decide from `/config.json`.

---

## 5. Access model in the UI

Roles: `Viewer` (all View* verbs), `Editor` (+ `EditCapturePolicy`), `Admin` (everything). Grants are scoped to Instance, Project, or Environment.

Build a single capability helper, e.g. `can(verb, { project, environment })`, fed from `/api/me` (extend it to return grants), and use it everywhere.

Rules:
- **Unreachable everywhere?** Hide it (nav items, admin group, action buttons).
- **Reachable in some scope but not this one, and the user arrived by link?** Show the in-place explanation (`Access.html`, panel B): what's needed, what they do have, a way to switch to a scope they can see.
- **Signed in but not admitted** (every `/api` call 403s because the email isn't pre-registered): full-screen state, `Access.html` panel A. Show the email with a Copy button, "ask an admin to add this", Reload, Use a different account. Never a raw 403.
- **Unauthenticated:** no landing page; redirect straight to the IdP.
- Bulk actions on the issue list are disabled until rows are selected (mockup shows the empty-selection state). For Viewers they're not rendered at all.

## 6. Enforcement mode in the UI

Modes: `advisory` (default), `enforcing`, `locked`. Projects may be stricter than the instance, never looser.

- Mode badge in the top bar on every screen.
- Any control the mode forbids is **rendered, disabled, with a lock icon and a reason**: see `Access.html` panel C (identity capture "Full" locked; masking forced to strict) and `Channels.html` (Slack/Telegram unavailable with the outside host named).
- Loosenings refused under enforcing/locked: identity capture in prod, sending sessions without consent, masking below strict, weakening RBAC, outbound/reachback channels, capture policy below floor, disabling audit. Get the list from the API if possible rather than hardcoding.
- `Access.html` panel D shows the mode as a three-step dial with the refused list; that's the Enforcement admin page content.

---

## 7. Screens

Suggested routes use a project prefix: `/p/:projectId/...`. Global filters live in the query string (`?env=prod&window=24h&version=1.8.2`).

### 7.1 Issues list: `static/Main.html`
Route `/p/:projectId/issues`. **API:** gap (`list issues`).
- Header: project label, "Issues" h1, filter bar.
- **Regression callout** (orange wash): appears only when the latest deployed version caused regressions. Text: count + version + how long ago; button "Review regressions" filters to `is:regressed`.
- Status tabs with counts: Unresolved, Regressed, Resolved, Muted. Search field accepts a query syntax (`is:unresolved service:api area:billing version:1.8.2`). Service / Area / Sort buttons open menus.
- Bulk action row: "N selected" + Resolve, Assign, Tag area, Mute.
- Table columns: checkbox · Issue (status pill, error type link, message truncated; second line: issue id, `service · location`, area tag, optional "minified frames, no source map" tag, status note like "regressed in 1.8.2 · resolved in 1.8.0") · 24 h sparkline (stroke color = status color) · Events · Sessions (`-` when no browser session, e.g. worker) · Last / first seen · Versions (`first → latest`).
- Footer: "Showing N of M · grouped from X events by fingerprint" + Load more (or virtualize).
- Row data shape: see `rows` in `source/Main.dc.html`.

### 7.2 Issue detail + correlation walk: `static/IssueDetail.html`
Route `/p/:projectId/issues/:issueId`. **API:** gap (`issue detail`).
- Breadcrumb, status pill (**Regressed in 1.8.2** is the emphasized form), service/area/env tags, error type h1, message in mono, location + lifecycle sentence ("resolved by X in 1.8.0 · recurred automatically 2h ago in 1.8.2").
- Actions (role-gated): Resolve/Reopen, Assign, Tag area, Mute, Copy link.
- **Correlation walk strip** (the most important component in the product): four linked cards for the latest occurrence: **1 Error** (current, orange) → **2 Trace** (id, root op, duration, span/service count) → **3 Logs on trace** (count, warn/error count, top message) → **4 Session replay** (session id + offset, device, masking level, primary button "Open replay at mm:ss"). A caption explains the link ("linked by session id ..., minted in the browser and stamped on the request"). Build it as a reusable `<CorrelationWalk current="issue|trace|logs|replay">` so the trace and replay pages can show the same thread. If a link is missing (no session: worker job; identity off; session not retained), render that card disabled with the reason.
- Occurrences chart: 24 bars, post-deploy bars orange, dashed deploy marker labeled with version and time.
- **Stack trace:** header with a **Symbolicated** pill and source ("portable PDB · api 1.8.2" or "source map app.min.js.map · 1.8.2"). If not symbolicated, show a warning pill and link to the Symbols settings page for that version. In-app frames expanded with ~5 lines of source context and the failing line highlighted; framework frames collapsed behind "N framework frames". Toggle "Raw frames".
- **Breadcrumbs:** browser and server events on one timeline, with a source tag (browser/server), kind (color by kind), message. Times relative to session start. Under strict masking, click targets show selectors, not labels.
- Side column: **Version history** (dot per version: first seen / resolved / clean / regressed with the regressed row highlighted), environment breakdown bars, details (assignee, area, service, fingerprint, last alert, links).

### 7.3 Trace: `static/Trace.html`
Route `/p/:projectId/traces/:traceId?span=:spanId`. **API: built** (`GET /api/projects/{projectId}/traces/{traceId}` returns spans + logs).
- Header: status pill, env/version tags, root operation h1, mono line with full trace id, start time, duration, span count, services. Buttons: back to issue (if linked), primary "Replay <session> at mm:ss" (if the trace carries `tamp.session.id`).
- **Waterfall:** name column indented 16 px per depth with service color chip; bar positioned by `start/total` and `duration/total`; error spans get a red outline and red name text; selected span row gets the orange wash. Time axis header. Legend for service colors.
- **Selected span panel:** attributes as key/value; `exception.issue` links to the issue, `tamp.session.id` links to the replay. Show "not captured · identity off in prod" style values rather than omitting privacy-gated attributes.
- **Logs on this trace** (`#logs` anchor): time, level (colored), service, message; ERROR rows washed red. Link "Open in log explorer".
- Data shapes: `spans`/`logs` in `source/Trace.dc.html`.

### 7.4 Session replay: `static/Replay.html`
Routes `/p/:projectId/replay` (session list, not mocked yet; see section 9) and `/p/:projectId/replay/:sessionId?t=42000`. **API: built** (list + `/sessions/{id}/events` for rrweb). Player wraps **rrweb-player** (already built as a basic wrapper).
- Header: session id, env, app version, browser/OS, viewport, start URL, duration, event count. Pills: **Masking level** (with tooltip explaining what was obfuscated and that it's irreversible) and **Identity** mode. Links to the correlated issue and trace.
- **Player frame:** rrweb iframe on a dark letterbox. Overlay label top-left: "reconstructed DOM, not video" + masking summary. Overlay top-right when the playhead is near a correlated server error: orange pill "Server error in 1.2 s · ISS-1042". The mockup's gray blocks show what a strict-masked session looks like; don't fake readable text.
- **Timeline** (custom, replaces rrweb-player's default controller): 
  - marker row above the track: thin ticks per event, colored by kind (nav gray, click cyan, console warn yellow, failed request red, correlated server error orange and taller);
  - activity histogram track (bucketed event density), played portion tinted;
  - idle stretches drawn hatched with a label "34 s idle · skipped" when Skip idle is on;
  - playhead line + draggable knob; click anywhere to seek; keyboard: Left/Right = 5 s, Space = play/pause.
- Controls: back 5 s, play/pause (primary), forward 5 s, time `mm:ss.s / mm:ss`, speed 1× 2× 4× 8×, Skip idle checkbox, **Previous error / Next error** (orange outline), fullscreen.
- **Event list** (right): filter chips All / Console / Network / Errors with counts; rows follow the playhead (current row highlighted, future rows dimmed); clicking a row seeks. Network rows show method+status, path, duration, and the trace id link when present.
- Desktop-first. On phones, show session metadata and the event list, with a note that playback needs a larger screen.
- Data shapes: `marks`, `events` in `source/Replay.dc.html`.

### 7.5 Notification channels: `static/Channels.html`
Route `/admin/channels`. Admin only. **API:** gap (channel config). Channels themselves are built (SMTP, Slack, Telegram).
- Posture banner (violet) when mode is enforcing/locked with no-reachback: one sentence on what's refused and what's still allowed, link to Enforcement.
- One card per channel: name, status pill (Enabled / Unavailable with lock), config fields (SMTP: relay host, from, to; Slack: webhook URL; Telegram: bot token + chat id), **Send test** button with last test result. Unavailable cards keep their fields visible but disabled, and name the outside host that makes them reachback.
- **Routing matrix:** rows = alert kinds (New issue, Regressed issue built; Spike, Threshold, Heartbeat tagged "planned"), columns = channels, checkboxes. Columns for unavailable channels are disabled.

### 7.6 Access & trust states: `static/Access.html`
Not a single route; a sheet of four reusable states plus the demo banner. See sections 5 and 6. Build A as `/not-admitted`, B as a `<ScopeDenied>` component, C as the pattern for any policy-locked control (`<LockedReason>`), D as the Enforcement admin page body.

### 7.7 Mobile triage: `static/Mobile.html`
390 px. Project switcher + mode badge in the header; env segmented control; regression callout; issue cards (status, last seen, type, 2-line message, location, event count); bottom tab bar with 44 px+ targets. No fake status bar. Resolve/assign/mute from the card's detail view.

---

## 8. Suggested component inventory

`AppShell`, `TopBar`, `ProjectSwitcher`, `CommandPalette`, `ModeBadge`, `UserMenu`, `SideNav`, `FilterBar` (`EnvSegmented`, `TimeWindowPicker`, `VersionPicker`), `StatusPill` (unresolved/regressed/resolved/muted), `Tag`, `Button` (default/primary/disabled), `Panel`, `SectionLabel`, `Sparkline`, `IssueTable`, `RegressionCallout`, `CorrelationWalk`, `OccurrenceChart`, `StackTrace` (+ `SymbolicationBadge`), `BreadcrumbList`, `VersionHistory`, `TraceWaterfall`, `SpanAttributes`, `LogList`, `ReplayPlayer` (wraps rrweb-player), `ReplayTimeline`, `ReplayEventList`, `MaskingPill`, `ChannelCard`, `RoutingMatrix`, `LockedReason`, `ScopeDenied`, `NotAdmitted`, `DemoBanner`, `EmptyState`, `LoadingState`, `ErrorState`.

Every data surface needs four states: loading (skeleton rows matching the layout), empty (one line saying why plus the next action), error (message + retry), permission-denied (section 5).

---

## 9. Not designed yet

Overview dashboard, Logs explorer (+ live tail), Metrics dashboards, Session list (index of replayable sessions), Alerts (rule builder + feed), Project settings (services, environments, versions, areas, capture policy, symbols), Users & roles admin, Storage & health. Follow the same shell, tokens and patterns; the brief (sections 6.1 to 6.12) has the requirements.

## 10. Known simplifications in the mockups

- The mock shows an Admin user, so everything is visible. Implement the gating in section 5.
- Sidebar wraps instead of becoming a drawer on narrow screens.
- Tabs, segmented controls and menus are static in the mockups; wire real state.
- Sparklines and histograms are hand-drawn polylines/bars; render from bucketed API data.
