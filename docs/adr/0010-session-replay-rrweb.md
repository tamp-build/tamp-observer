# ADR 0010: Session replay via rrweb, with a metadata/blob storage split

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-10

## Context and Problem Statement

Session replay is the browser archetype that ADR 0009 granted its own front door. We need to decide how a
session is captured, how it is stored given our tiering (ADR 0005/0006), how it correlates to backend
errors, and what the privacy posture is, especially for ITAR where a replay could otherwise capture
export-restricted data.

highlight.io solved this with rrweb, and much of the work is integration rather than invention. But its
SaaS shape made some choices we must change (notably where capture is decided, ADR 0011) and its storage
assumptions have to map onto our tiers.

This ADR fixes the capture technology, the storage split, the correlation key, and the capture-time privacy
posture. It does not decide *when* a session is recorded (that is the smart-capture decision, ADR 0011).

## Decision Drivers

* **Replay must be inspectable and small, not video.** A structured event log is smaller, searchable, and
  privacy-tractable in a way frames are not.
* **The firehose must not pollute the analytical store.** DOM-mutation streams are high-volume opaque
  payloads; putting them in the queryable DB would wreck it (ADR 0005).
* **Correlation to backend errors is the money feature.** Jumping from a server exception to the exact user
  replay is the differentiator; the key for it must be designed in early.
* **ITAR-grade privacy at capture.** Sensitive data must be stripped client-side before anything ships, with
  a hard guarantee available, not a best-effort regex.

## Decision

### 1. rrweb DOM reconstruction, not video

Capture is rrweb (MIT): on session start, serialize the full DOM to JSON (tree + computed styles) so replay
reconstructs appearance without fetching real assets; then a `MutationObserver` records every DOM change as
a timestamped diff, plus interaction events (mouse/click/scroll/input/viewport) on their own timeline.
Replay loads the snapshot into a sandboxed iframe and plays the mutation + interaction stream in time order.
Console and network breadcrumbs interleave on the same timeline. This is a structured event log, not frames:
smaller and inspectable.

### 2. Bursty chunked delivery over the session endpoint

Replay ships in batched chunks over the session's life (not one per mutation), continuing for minutes to
hours. This bursty cadence is unlike a backend exporter and is exactly why the session archetype has its own
front door (ADR 0009) and its own ingestion path.

### 3. Storage split: metadata in the analytical store, payload as a blob

Session data splits onto our tiering (ADR 0005/0006):

* **Session metadata** (duration, UA, page, error count, device, searchable attributes) goes in the
  queryable analytical store. This is what search (and the "which session is worth replaying?" question)
  runs against.
* **Replay payload** (the DOM-mutation firehose) is an opaque blob in bulk/blob storage, chunked, fetched by
  session id on demand.

We query the index to find the session worth replaying; we fetch the blob to replay it. The firehose never
touches the analytical DB.

### 4. Session id and the cross-archetype correlation key

Session id is a client-minted opaque UUID at session start (not a user, not an IP; ADR 0007). The frontend
passes its session id on outbound requests via a header; backend errors carry the session id too. That lets
a server exception link to the exact replay of the user who hit it. This cross-archetype key is designed in
now, early, because retrofitting it is expensive and it is the differentiator.

### 5. Privacy at capture: client-side masking, strict-by-default for ITAR

Masking happens client-side at record time, before any chunk ships:

* strict mode obfuscates all text and images irreversibly, client-side;
* default mode masks inputs plus regex-matched PII, with `-mask` / `-block` / `-ignore` class hooks;
* heavy/sensitive signals (network request/response bodies and headers, canvas) are opt-in, off by default.

**For ITAR the posture is strict-mode-by-default plus client-side stripping plus allowlist-not-blocklist
masking.** Note explicitly: the regex default is best-effort and misses names and non-text/canvas PII;
**strict is the only hard guarantee.** This is why the enforcement surface (ADR 0002) refuses sub-strict
masking under locked mode.

## Consequences

### Positive

* Replay is small, searchable, and inspectable, and reconstructs without fetching customer assets.
* The analytical store stays healthy because the mutation firehose lives in blob storage, fetched on demand.
* Error-to-replay correlation works because the session-id key is wired through from the start.
* ITAR sites get a hard privacy guarantee (strict mode) enforced non-negotiably under locked mode.

### Negative / accepted costs

* **Two storage destinations per session** (metadata index + payload blob) and a fetch-by-id path to stitch
  them. Accepted: it is the only way to keep search fast without drowning the DB in the firehose.
* **Strict mode reduces replay fidelity** (obfuscated text/images). Accepted and correct for ITAR: fidelity
  is not worth leaking export-restricted data, and non-strict modes remain available where policy allows.

### Neutral / future-facing

* The replay player is framework-agnostic JS wrapped as a Svelte component (ADR 0014); prototyping the rrweb
  Svelte wrapper early to confirm scrub-timeline ergonomics is called out there.
* The build cost here is mostly the chunk-stream ingestion path, the metadata/blob split, the session-id
  correlation, and the replay UI, not rrweb itself.

## Links

* The session front door this rides: ADR 0009 (TOBS-9).
* When a session is actually recorded (rolling buffer, server trigger, consent): ADR 0011 (TOBS-11).
* Storage tiers and the write/read abstraction the split maps onto: ADR 0005 (TOBS-5), ADR 0006 (TOBS-6).
* Session id as an event-stamped dimension: ADR 0007 (TOBS-7).
* Strict-masking enforced under locked mode: ADR 0002 (TOBS-2).
* Replay player as a Svelte-wrapped JS component: ADR 0014 (TOBS-14).
* Originating design note: `docs/holdfast-architecture.md`, "Session replay (the browser archetype, in
  depth)".
