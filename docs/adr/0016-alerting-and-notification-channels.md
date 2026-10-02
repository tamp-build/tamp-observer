# ADR 0016: Alerting on new/regressed Issues, with pluggable notification channels

* Status: Proposed
* Date: 2026-10-02
* Deciders: scott
* Tracking: TOBS-17

## Context and Problem Statement

The Issue model (ADR 0015) turns a stream of errors into actionable Issues. The next step is telling
someone: fire an alert when a **new** Issue appears or a resolved one **regresses**. Two questions:
what detects the alert, and where does it go.

Deployments span the full audience (ADR 0001): a dev shop wants Slack or Telegram in minutes; a
locked-down enclave must not reach any cloud. The architecture doc's first cut said "air-gap-appropriate
channels only (NO cloud notifier)". That is the ceiling's stance, not the identity. Floor-to-ceiling
means we **offer many channels** and let the deployment's enforcement posture decide which are allowed.

## Decision Drivers

* **One alerting core, many sinks.** The rule that decides "this is alert-worthy" is engine logic; the
  delivery is a channel. They must be decoupled so channels are added without touching the core.
* **Operators enable the channels they want.** Zero, one, or several, from whatever the build supports.
* **Delivery is best-effort and must never block ingest.** A down Slack webhook cannot stall the pipeline.
* **Air-gap is a posture, not a fork.** Cloud channels are reachback; in a locked enclave they must be
  genuinely unavailable, using the enforcement gate (ADR 0002), not a separate code path.

## Decision

### 1. An alerting core that emits alert events

The Issue projection (ADR 0015) is the rule source for this iteration: when it **creates** an Issue it
emits a `NewIssue` alert; when it flips an Issue to **Regressed** it emits a `RegressedIssue` alert. Each
alert is a small, channel-agnostic record (project/service/issue ids, title, error type, count, version).
Richer rules (error-rate spikes, thresholds, silence/heartbeat) are additive on the same core later.

### 2. Pluggable channels behind one interface

Delivery is a dial, like storage (ADR 0006) and the raw bucket (ADR 0004): one `INotificationChannel`
(`Name`, `IsReachback`, `SendAsync`). Implementations ship for **SMTP, Telegram, and Slack** now; internal
webhook, syslog, and a dashboard badge are future. The operator **enables one or more** supported channels
by configuration; the core does not know or care which.

### 3. A dispatcher fans out to the enabled channels

An `IAlertDispatcher` holds the enabled channels and sends each alert to each. Per-channel failures are
isolated and swallowed (best-effort; a bad channel never blocks the others or the pipeline). Dispatch runs
after the admit write succeeds, off the critical path.

### 4. Enforcement decides which channels are allowed (air-gap reconciliation)

A channel declares whether it is **reachback** (leaves the installation's network). Cloud channels
(Telegram, Slack) are reachback; an internal SMTP relay, internal webhook, and syslog are not. The
dispatcher asks the enforcement gate (ADR 0002): under enforcing/locked, `Loosening.ComponentReachback`
is refused, so reachback channels are **skipped entirely** (the path is absent, not merely disabled).
This is the concrete reconciliation of the doc's "no cloud notifier": cloud channels are offered, and are
unavailable precisely when the deployment is locked down. Non-reachback channels always deliver.

### 5. Channel configuration

Each channel is configured by its own settings (endpoint/token/recipients), the same per-component shape
the OTel collector exporters use. Secrets come from configuration / the environment; nothing is hardcoded.

## Consequences

### Positive

* Alerting value in minutes for a dev shop (enable Slack/Telegram), and a safe default for an enclave
  (cloud channels structurally off under lock), from one codebase.
* New channels are isolated additions implementing one interface.
* A flaky channel degrades to "that channel missed an alert", never to a stalled pipeline.

### Negative / accepted costs

* **Best-effort delivery can drop an alert** (channel outage). Accepted for now; a durable outbox with
  retry is a later hardening, not this iteration.
* **The reachback classification is per-channel and coarse** (e.g. external SMTP is really reachback too).
  Accepted: the common internal-relay case is right, and operators pick channels deliberately.

### Neutral / future-facing

* Spike/threshold/heartbeat rules, alert routing per Environment/Project, de-duplication and rate limiting,
  and a durable outbox are future work on the same core.
* Dispatch currently uses the instance-level enforcement gate for the reachback decision (reachback is a
  deployment property); per-project overrides can refine it later.

## Links

* The rule source (new/regressed detection): ADR 0015 (TOBS-16).
* The enforcement gate that gates reachback channels: ADR 0002 (TOBS-2).
* Channel-config shape mirrors the collector exporter config: ADR 0003.
* Originating design note: `docs/holdfast-architecture.md`, "App feature inventory" (Alerting).
