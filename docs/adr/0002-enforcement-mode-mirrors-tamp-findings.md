# ADR 0002: Enforcement mode mirrors tamp.findings (advisory/enforcing + locked floor)

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-2

## Context and Problem Statement

tamp-observer must serve the full audience span (ADR 0001): a small dev shop that wants a friendly,
fully-configurable default, and a defense/gov/FedRAMP enclave that needs a non-negotiable, assessable
posture. These two want opposite things from the same binary. The dev shop wants every knob loose and
present; the enclave wants the dangerous knobs to not exist.

tamp.findings already solved the equivalent problem for gating, in its ADR 0004: a strictness axis
`advisory < enforcing`, stored on instance settings, with a `locked` flag that turns the instance
setting into a floor no project can weaken. The design intent recorded in `holdfast-architecture.md`
is explicit: observer should run the **same way, with the same vocabulary and semantics**, and must
**not invent a parallel switch**.

The handoff doc assumed tamp.findings implemented this as a startup **environment variable** (and left
a TODO to look up "the exact var name + accepted values"). That assumption is wrong: tamp.findings uses
instance **configuration**, not an env var. This ADR resolves the conflict in favor of the intent
(mirror the real mechanism and vocabulary) over the mistaken detail (an env var), and pins the two
places where observer legitimately differs from tamp.findings.

The question this ADR answers: **how does an operator declare enforcement posture in tamp-observer, and
what exactly does that posture govern?**

## Decision Drivers

* **Mirror tamp.findings, do not fork it.** The ecosystem must present one enforcement concept across
  pillars. An operator who learns `advisory`/`enforcing` + `locked` in findings must find the same in
  observer, with the same meaning.
* **Do not hobble the community.** A fresh install is advisory. The small shop never sets the switch and
  never needs to know the regime exists.
* **Locked must be trustworthy to an assessor.** In a locked enclave, "the loosening is just set to
  strict" is not good enough; a value that can be flipped is a value that will be flipped. The loosening
  path has to be genuinely unreachable.
* **Enforcement posture and scale are orthogonal.** The switch governs the security/enforcement surface
  only, never storage tier, buffer tier, or ClickHouse-vs-Postgres. A small defense subcontractor may
  run locked-on-Postgres-floor; a funded startup may run advisory-on-ClickHouse.
* **No Client entity to resolve through.** Unlike tamp.findings, observer has no Client/Tenant layer
  (the installation is the client), so the resolution chain is one layer shorter.

## Invariants

These hold for every change on this track. If a design choice breaks one, the choice is wrong.

1. **Default is advisory.** A fresh install is friendly, permissive, fully configurable. The switch is
   absent by default and the dev shop never sets it.
2. **Same vocabulary as tamp.findings.** The modes are `advisory` and `enforcing`; the floor flag is
   `locked`. No synonyms, no third `off` mode (off is simply advisory with nothing tightened).
3. **Locked loosening paths are ABSENT, not defaulted.** Under a locked, enforcing instance, code must
   not consult the loosening/override config for any enforcement-surface toggle at all. The gate is
   checked at the mode boundary, so the loosening cannot happen, not merely should not.
4. **The switch governs the enforcement surface only.** It never touches the scale/performance surface.
   The two are independent axes.
5. **Mirror the mechanism, not a parallel one.** Enforcement posture lives in instance configuration,
   the same shape tamp.findings uses. No env-var switch, no observer-only control plane for this.

## Decision

### 1. Two modes on one strictness axis, in instance configuration

`advisory < enforcing`, stored on instance settings as `enforcement { mode, locked }`, shipping
`advisory` / `false`. This matches tamp.findings ADR 0004 field-for-field.

* **`advisory`** (default): the friendly, permissive, fully-configurable dev-shop posture. Every
  enforcement-surface knob is present and tunable. Observer still reports and still works; nothing is
  blocked or stripped on policy grounds.
* **`enforcing`**: the locked-down posture. Enforcement-surface loosenings are refused.

There is deliberately no env var. The handoff doc's "match the env var to the letter" is superseded
here: tamp.findings has no such var, and inventing one for observer would be the "parallel switch" the
doc told us not to build.

### 2. The locked floor, resolved Project -> Instance

Per-project posture may be set on the project's config (optional; omitted = inherit). Resolution is
**Project -> Instance default**, most-specific wins, with one exception:

* **Unlocked** (OSS default): most-specific-wins; a project may set advisory or enforcing freely.
* **Locked**: the instance mode becomes a **floor**. Effective mode = the stricter of (instance floor,
  project setting). A project may match or exceed the floor; it cannot weaken it.

This is tamp.findings' locked-floor semantics exactly, with **one documented divergence**: tamp.findings
resolves Project -> Client -> Instance, observer resolves **Project -> Instance**, because observer has
no Client entity (the installation is the client; see the entity-model ADR). The vocabulary and the
floor behavior are identical; the chain is one layer shorter.

### 3. What the mode governs: the enforcement surface

In `enforcing` (and non-weakenably so under `locked`), these loosenings are refused / absent:

* identity capture cannot be enabled in production;
* session data cannot transmit without consent;
* PII masking cannot drop below strict;
* auth cannot be less than the in-enclave RBAC posture;
* no component may be given a reachback path;
* capture policy cannot relax below its floor;
* audit (once it exists) cannot be disabled.

For each of these, invariant 3 applies: under locked the code path that would loosen the toggle is not
consulted. The check lives at the mode boundary, not scattered across call sites.

### 4. What the mode does NOT govern: the scale/performance surface

Storage tier, buffer/raw-bucket tier, ClickHouse-vs-Postgres, and how far a site scales are untouched
by this switch. They are a separate axis, decided per-project on operational grounds (see the storage
tiering ADR). Enforcement posture and scale compose freely in all four combinations.

## Consequences

### Positive

* One enforcement concept across tamp pillars. Learning it in findings transfers to observer verbatim.
* The enclave gets an assessable guarantee: locked loosenings are structurally unreachable, not merely
  set strict.
* The dev shop is never hobbled and never meets the regime; the default is the friendly one.
* Posture and scale stay independent, so neither constrains the other.

### Negative / accepted costs

* Invariant 3 ("absent, not defaulted") is more than a config default: it requires the loosening paths
  to be gated at a single boundary and genuinely skipped under locked. That is real design discipline,
  paid on purpose, because it is the whole value for an assessor.
* A per-toggle audit is needed to confirm every enforcement-surface loosening actually routes through
  the mode boundary. That audit is part of building this surface, not a separate initiative.

### Neutral / future-facing

* The concrete field path for per-project posture and the exact name of the instance config object are
  settled when the config model is built; this ADR fixes the vocabulary (`advisory`/`enforcing`,
  `locked`), the resolution chain (Project -> Instance), and the surface, not the serialization.
* If observer ever grows a multi-client discriminator (explicitly out of scope in ADR 0001), the
  resolution chain would gain a layer and should re-converge on tamp.findings' Project -> Client ->
  Instance. Noted so a future ADR revisits this rather than re-deriving it.

## Links

* Source of the mirrored mechanism and vocabulary: tamp.findings ADR 0004 (gate enforcement modes and
  the locked floor).
* Pillar framing and audience span this serves: ADR 0001.
* The Client-less entity model that shortens the resolution chain: entity-model ADR (TOBS-7, planned).
* The scale/performance axis this switch is orthogonal to: storage-tiering ADR (TOBS-5, planned).
* Enforcement-surface toggles governed here are defined in: capture-policy (TOBS-12), auth (TOBS-13),
  and smart-capture/consent (TOBS-11) ADRs, all planned.
* Originating design note: `docs/holdfast-architecture.md`, "ENFORCEMENT MODE" block.
