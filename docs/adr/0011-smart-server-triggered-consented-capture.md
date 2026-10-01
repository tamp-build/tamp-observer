# ADR 0011: Smart, server-triggered, consented capture (rolling buffer, pull channel, host-callback consent)

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-11

## Context and Problem Statement

ADR 0010 fixed how a session is captured and stored. This ADR decides *when*, and that decision is the
genuine differentiator over highlight.io. The objection that killed the original highlight proposal was
session data stored by a third party in the cloud. Because tamp-observer owns the whole pipe (no third
party), the server can decide when a session is worth persisting and signal the client in near-real-time,
something a SaaS structurally cannot do.

The hard problems are: catching the lead-up to an error (not just the aftermath), deciding server-side when
to record, getting a directive back to the client (backends do not normally need a return channel), and
obtaining consent without injecting our UI into someone else's app. And all of this has to respect the
enforcement surface (ADR 0002): under locked mode, session data cannot transmit without consent.

## Decision Drivers

* **Capture the lead-up, not just the aftermath.** A trigger that fires on an error has already missed the
  seconds before it, which are the useful part.
* **Decide server-side on data we already have.** The evaluator already watches the error stream; the
  trigger should be a small rule on that, not new client machinery.
* **Minimize standing cost and connections.** Thousands of active sessions cannot each hold a server
  connection, and nothing should persist or transmit until there is a reason.
* **Never inject our UI into the host app.** We do not know their stack, and injecting DOM breaks layouts
  and spooks reviewers. Consent must be the host's to render.
* **Honor the enforcement surface.** Under locked mode, no session transmits without consent, and that path
  must be absent, not merely defaulted (ADR 0002).

## Decision

### 1. Keystone: a rolling client-side ring buffer that never transmits until triggered

The client always lightly records into a bounded in-memory ring buffer: the last N seconds of DOM mutations
plus breadcrumbs, continuously overwritten, **never transmitted**. A trigger flips it from
buffering-and-discarding to flush-the-buffer-and-stream. This is the only way server-triggered capture
catches the lead-up to the error rather than just the aftermath. It is cheap, memory-only, and
privacy-friendly: nothing persists or transmits until a reason exists. This buffer is the keystone the rest
of the design depends on.

### 2. Server-side trigger in the evaluator

The .NET evaluator is already watching the error stream (ADR 0004), so detecting a per-session anomaly (an
error-rate spike over a baseline) is a small rule on data we already ingest. On trigger, it sets a
per-session recording flag. The baselines this needs (per-Service+Version error rates) are a modest standing
evaluator workload, specified with the capture policy in ADR 0012.

### 3. A bidirectional client channel, pull not push

The JS SDK listens for directives keyed to its session id. This is new architecture beyond pure OTLP export
(backends do not need a return channel). We choose **pull** (the client polls "should I record?" every few
seconds) over **push** (a held SSE/websocket per session):

* a few seconds of lag is fine, because the rolling buffer (§1) backfills the lead-up anyway;
* pull avoids a persistent connection per active session.

The keystone buffer is precisely what lets us pick the cheaper channel.

### 4. Consent is a host-app callback, never SDK-rendered UI

The SDK never renders consent UI. It exposes a hook ("recording requested for this session; call
approve()/deny()") and waits for the host's verdict before flushing. The host decides how to ask (their
modal, their copy) or pre-approves by policy (internal/defense apps where consent is by employment/policy and
the prompt is skipped). We provide the mechanism; the host sets the policy. We never inject our DOM into
their page.

### 5. Enforcement-surface tie-in

Under locked enforcing mode (ADR 0002), the path that would transmit session data without a consent verdict
is absent. Consent is not a toggle that can be defaulted off under locked; the no-consent-no-transmit gate is
checked at the mode boundary. Pre-approval-by-policy is still available (it is a consent verdict, supplied by
the host), but silent transmission is not.

## Consequences

### Positive

* We capture the lead-up to errors, which is the useful window, via the rolling buffer.
* The trigger is nearly free: a rule on the error stream the evaluator already processes.
* Standing cost is minimal: memory-only buffering client-side, polling instead of held connections.
* Consent respects the host's UI and policy, and the no-transmit-without-consent guarantee is assessable
  under locked mode.

### Negative / accepted costs

* **A return channel to the client is new surface** beyond OTLP export. Accepted: it is the mechanism of the
  differentiator, kept cheap by choosing pull over push.
* **Polling adds a few seconds of latency** before a flagged session starts streaming. Accepted by design:
  the rolling buffer backfills that window, so the lag costs nothing material.
* **Host integration work for consent** falls partly on the customer (they wire the callback). Accepted: it
  is the only way to avoid injecting our UI, and defense/internal apps skip the prompt via policy anyway.

### Neutral / future-facing

* The concrete trigger models and numbers (error-rate thresholds, z-score/multiplier, absolute counts) are
  operator-tunable policy specified in ADR 0012, not hard-coded here.

## Links

* How a session is captured/stored once recording starts: ADR 0010 (TOBS-10).
* The evaluator that hosts the trigger and the baselines: ADR 0004 (TOBS-4), ADR 0012 (TOBS-12).
* Enforcement surface (no transmit without consent under locked): ADR 0002 (TOBS-2).
* Operator-tunable trigger rules and capture policy: ADR 0012 (TOBS-12).
* Originating design note: `docs/holdfast-architecture.md`, "Smart, server-triggered, consented capture".
