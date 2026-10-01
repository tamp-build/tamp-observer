# ADR 0012: Capture policy bound to Environment (operator knobs)

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-12

## Context and Problem Statement

ADR 0011 established server-triggered capture and said the trigger rules and baselines are operator-tunable
policy specified here. We need to decide where capture policy binds, what it contains, and how it behaves as
a Version moves between environments. "Record everything" is right for QA and ruinous for prod (PII and
volume); "record nothing" is safe for prod and useless for a beta test. So capture has to be policy, scoped
somewhere sensible, and tunable without redeploying the client SDK.

ADR 0007 already made Environment the one discovered entity that carries behavior, precisely so it can be
this scope. This ADR fills that in.

## Decision Drivers

* **One unambiguous scope for policy.** "Which policy applies to this event?" must have exactly one answer.
* **Environment-shaped differences.** QA wants max detail; prod wants lean. That difference is per
  environment, so policy belongs on Environment.
* **Tunable without a client redeploy.** "Normal" is wildly app-specific; operators must tune thresholds
  server-side without shipping a new SDK build.
* **Promotion is a fact, not an action.** The same Version showing up in prod instead of QA is observed from
  telemetry; policy must react to that, not require a human to "promote."
* **Respect the enforcement surface.** Capture policy cannot relax below a floor under locked mode (ADR
  0002).

## Decision

### 1. Policy binds to Environment, optionally overridden per Service

Capture policy binds to **Environment**, with an optional per-Service override; the default scope is
Environment. This is why Environment is a first-class, policy-bearing axis (ADR 0007). It gives policy one
home and one resolution answer.

### 2. Policy is a config bundle

* **Baseline mode**: record-always-max-detail (QA/beta) / record-errors-only / record-nothing-until-triggered
  (the prod default).
* **Trigger rules**: operator-tunable outlier-spike definitions, because "normal" is app-specific. Expose
  concrete models and numbers: errors-per-minute-per-session over X, rate over Y-times-baseline (z-score /
  multiplier), absolute count in a window. Declarative, server-side, operator-authored, so they tune without
  redeploying the client SDK (ADR 0011 §3).
* **On-trigger actions**: start session recording (flush buffer + stream, ADR 0011) and/or alert.
* **Detail level**: DOM only / DOM + network bodies (opt-in heavy) / console / etc. QA max; prod lean (PII +
  volume).

### 3. Beta test is a preset, not a feature

A beta test is simply a policy preset (record-always-max-detail, optionally time-boxed), not special code.
This keeps the surface small: beta is a configuration of the same knobs.

### 4. Promotion is observed, not performed

The same Version appearing under a new Environment (telemetry arrives with `deployment.environment=prod`
instead of `qa`) is how promotion is detected. Each Environment applies its own policy. Version is stable
across environments (so a QA-caught bug recurring in prod is computable, ADR 0007/0008); Environment scopes
the policy. No human "promotes" anything; the policy follows the telemetry.

### 5. Standing baseline workload

Trigger rules that compare against a baseline imply computing per-Service+Version error-rate baselines. That
is a modest standing workload on the evaluator (ADR 0004/0011), accepted as the cost of baseline-relative
triggers.

### 6. Enforcement-surface floor

Under locked enforcing mode (ADR 0002), capture policy cannot relax below its floor: identity capture cannot
be enabled in prod, detail level cannot exceed what strict masking allows (ADR 0010), and the loosening paths
are absent at the mode boundary, not merely defaulted. Advisory mode exposes the full range of knobs.

## Consequences

### Positive

* Policy has exactly one home and one resolution answer (Environment, optional per-Service override).
* Operators tune triggers and detail server-side without shipping a client build.
* QA-to-prod promotion needs no human action; policy follows the observed environment automatically.
* Beta testing adds no new surface; it is a preset of existing knobs.

### Negative / accepted costs

* **Standing baseline computation** per Service+Version on the evaluator. Accepted: it is modest and is the
  prerequisite for baseline-relative triggers, which are the useful ones.
* **Operators must author thresholds**, and bad numbers mean noisy or silent triggers. Accepted: "normal" is
  app-specific and cannot be hard-coded; concrete models and defaults reduce the burden.

### Neutral / future-facing

* Identity-mode (off / pseudonymous-hash / full) is a capture-policy field with a per-Service override,
  default off; its default-state decision is finalized in the auth/identity ADR 0013, which owns identity.
* Retention policy ("keep prod errors 90 days, QA sessions 7 days, drop old builds") is related but is an
  admin lifecycle surface over the storage tiers; it is named in the app-feature inventory and not fixed
  here.

## Links

* The trigger mechanism and consent this policy drives: ADR 0011 (TOBS-11).
* Environment as the policy-bearing scope; Version stable across environments: ADR 0007 (TOBS-7), ADR 0008
  (TOBS-8).
* The evaluator that computes baselines and applies policy: ADR 0004 (TOBS-4).
* Enforcement floor under locked mode: ADR 0002 (TOBS-2); strict masking detail cap: ADR 0010 (TOBS-10).
* Identity-mode default (off/pseudonymous/full): ADR 0013 (TOBS-13).
* Originating design note: `docs/holdfast-architecture.md`, "Environment capture policy (operator knobs)".
