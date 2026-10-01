# ADR 0013: External-only authentication, native RBAC, audit via a single chokepoint

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-13

## Context and Problem Statement

tamp-observer holds sensitive data (errors, replays, possibly ITAR-adjacent context) and runs from a dev
shop up to a locked-down enclave. It needs authentication, authorization, and (eventually) audit that work
across that whole span and satisfy an assessor at the top end. Three questions have to be answered together:
where identities come from, how permissions are modelled, and how audit avoids being a retrofit.

Two constraints shape the answers. First, "we don't hold credentials" is a feature here: a local password
store is a liability in an accredited environment. Second, an air-gapped site cannot reach GitHub or any
cloud IdP, so an external-IdP strategy has to include an in-enclave option or it does not actually serve the
ceiling.

## Decision Drivers

* **Don't hold credentials.** No local auth, no local password store. Authentication is always external.
* **Air-gap reality.** GitHub OIDC assumes reachability to GitHub, which a truly air-gapped site lacks, so a
  definable/in-enclave IdP is load-bearing, not a nicety.
* **External IdP roles don't model our resources.** An IdP knows users and groups; it does not know Projects,
  Environments, or capability verbs, and cannot be relied on for them in locked-down tenants.
* **Audit is a compliance requirement at the top end** ("who viewed which replay, when"), and retrofitting it
  into many call sites is painful and error-prone.

## Decision

### 1. Authentication: always external, no local credential store

AuthN is always external; there is no local auth and no local password store ("we don't hold credentials" is
the feature). Ship with **GitHub OIDC** as the MVP. Add a **definable OIDC provider** next (their
AD/Keycloak/etc). Note explicitly: the GitHub-only MVP effectively targets connected/dev deployments;
definable/in-enclave OIDC is required for actual air-gapped installs, so it is load-bearing, not optional.

### 2. Authorization: native tamp-observer RBAC from the start

AuthZ is **native RBAC, built in from the start**. Do **not** rely on external IdP roles (e.g. Entra): they
do not model our resources and cannot be relied on in locked-down tenants. Roles/permissions are modelled
around tamp-observer resources: Project / Environment plus capability verbs (view-replay, edit-policy,
manage-users, view-errors, and so on). The IdP authenticates and may optionally supply group claims; a
mapping layer (later) maps groups to roles as **inputs**, never as the role model itself.

### 3. Audit: deferred, but routed through one chokepoint now

Audit logging is deferred, but every authorization check routes through a single chokepoint seam now,
`IAuthorizationService.Check(...)`, so audit later becomes "emit from the chokepoint" rather than a retrofit
into many call sites. In ITAR contexts "who viewed which replay, when" is likely a compliance requirement, so
the seam is built even though the emission is not yet.

### 4. Identity-mode default (owned here, used by capture policy)

User/segment identification is a capture-policy field (ADR 0012) with a per-Service override, but identity is
owned here. Identity-mode is off / pseudonymous-hash / full, and is **default off**. It is distinct from the
always-present session id, so sessions degrade gracefully when identity is off (replay still works, just not
linked to a named user). Default-off is the right posture for prod/ITAR; turning it on is a deliberate,
auditable act. All three modes are exposed; the recommended default is off-entirely.

### 5. Enforcement-surface tie-in

Under locked enforcing mode (ADR 0002), auth cannot be less than the in-enclave RBAC posture, and identity
capture cannot be enabled in production. These loosening paths are absent at the mode boundary, not defaulted.

## Consequences

### Positive

* No credential store to secure, breach, or accredit; authentication is someone else's hardened IdP.
* Permissions model our actual resources, so authorization is meaningful and not hostage to IdP role
  semantics.
* Audit becomes a small additive change (emit from the chokepoint) instead of a sprawling retrofit.
* Identity defaults to off, so the safe posture is the default and enabling it is a deliberate, auditable act.

### Negative / accepted costs

* **The GitHub-only MVP does not serve air-gapped sites**; definable/in-enclave OIDC must land before the
  ceiling is real. Accepted and explicitly sequenced, not hidden.
* **Native RBAC is more to build** than leaning on IdP roles. Accepted: IdP roles cannot model our resources
  and cannot be trusted in locked tenants, so there is no shortcut here.
* **The group-to-role mapping layer is future work**; until it lands, role assignment is managed natively.
  Accepted: mapping is an input convenience, not the model.

### Neutral / future-facing

* The concrete capability-verb set grows with the app features (dashboards, tracing UI, log explorer) that
  introduce new actions to authorize; the seam and the Project/Environment scoping are fixed here.
* VisibilityScope bootstrap posture (what a brand-new install can see before the first role grant) is an
  implementation concern to pin when RBAC is built; noted so it is not forgotten.

## Links

* Enforcement surface (auth floor, identity-in-prod) governed under locked: ADR 0002 (TOBS-2).
* Identity-mode as a capture-policy field: ADR 0012 (TOBS-12).
* Resources that RBAC scopes to (Project, Environment): ADR 0007 (TOBS-7).
* Session id that stays present regardless of identity mode: ADR 0010 (TOBS-10).
* Originating design note: `docs/holdfast-architecture.md`, "Auth / identity / access".
