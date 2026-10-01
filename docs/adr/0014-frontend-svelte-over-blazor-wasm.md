# ADR 0014: Frontend is Svelte over the .NET OpenAPI API, chosen over Blazor WASM

* Status: Proposed
* Date: 2026-10-01
* Deciders: scott
* Tracking: TOBS-14

## Context and Problem Statement

tamp-observer is a .NET house, so the reflexive frontend choice is Blazor WASM (stay in C# end to end). But
the product has to run in locked-down, STIG'd environments, it leans heavily on JS-native libraries (rrweb
for replay, flamegraph and virtualized-log viz), and it targets modest air-gapped hardware. The frontend
framework has to survive all three, and "it's the same language as the backend" does not, on its own,
outweigh them.

This ADR decides the frontend framework and how it talks to the backend.

## Decision Drivers

* **STIG/locked-down browsers may block WebAssembly.** If a policy layer blocks WASM, a WASM app is bricked.
  A frontend that cannot run in the target environment is disqualifying, whatever its language.
* **The product is JS-library-heavy.** rrweb (replay) and the heavy viz (flamegraph, virtualized logs) are
  JS. The frontend should host them without interop-marshaling awkwardness.
* **Modest air-gapped hardware.** Small bundles with no heavy runtime matter.
* **Team already runs it in production.** A stack the team ships today is lower risk than one adopted for
  language symmetry.
* **Mobile-web triage matters.** Responsive triage on phones should be a clean fit.

## Decision

### 1. Svelte over the .NET OpenAPI API, with a generated typed TS client

The frontend is **Svelte**, talking to the .NET backend through its OpenAPI API, with a typed TypeScript
client generated from the OpenAPI spec. This keeps a clean contract between a JS frontend and the .NET
backend without hand-written client code.

### 2. Chosen deliberately over Blazor WASM

Blazor WASM is rejected for concrete reasons, not taste:

* locked-down/STIG'd environments sometimes block WebAssembly at the browser/policy layer, which would brick
  a WASM app; plain compiled JS is reliably permitted;
* Svelte ships small, no-runtime bundles (good for modest air-gapped hardware);
* Svelte is the native home for rrweb and the viz libraries;
* it is a stack the team already runs in production;
* it gives better mobile-web support.

The language-symmetry argument for Blazor does not outweigh "may not run at all in the target environment."

### 3. Heavy JS components wrapped as Svelte components

The rrweb replay player and the heavy viz (flamegraph, virtualized logs) are framework-agnostic JS wrapped as
Svelte components. This is clean, with no interop-marshaling awkwardness (a cost Blazor WASM would add for
exactly these JS-native pieces).

### 4. Mobile is triage, not replay

Responsive-web triage (issues, error detail, dashboards, alerts, search) is a clean fit on mobile. Session
replay works on mobile but is best on a larger screen (reconstructing a desktop-sized DOM; thumb-scrubbing a
timeline on a 390px viewport is inherently constrained). So mobile is triage plus lighter views; replay is a
larger-screen experience.

## Consequences

### Positive

* The frontend runs where it must: plain compiled JS is permitted even where WASM is policy-blocked.
* rrweb and the viz libraries are hosted natively, with no interop marshaling.
* Small no-runtime bundles suit modest/air-gapped hardware.
* The team ships Svelte today, so this is a low-risk, known stack, with good mobile-web triage.

### Negative / accepted costs

* **Two languages across the stack** (C# backend, TS/Svelte frontend) instead of C# end to end. Accepted: the
  OpenAPI-generated typed client keeps the seam clean, and the WASM-may-not-run risk makes Blazor the larger
  cost.
* **A JS build/toolchain** to maintain alongside .NET. Accepted: it is standard, and dogfooding the tamp
  ecosystem's JS tooling (per the project's build rules) is in scope anyway.

### Neutral / future-facing

* The large unbuilt UI surfaces (trace waterfall/flamegraph, log explorer with live tail, dashboards, the
  error-to-replay correlation walk) are named here only as what the frontend must eventually host; their
  design is their own future work, not fixed in this ADR.
* Prototype the rrweb Svelte wrapper early to confirm scrub-timeline ergonomics before committing deep (the
  one replay-UI reality check worth front-loading).

## Links

* The replay player this frontend wraps: ADR 0010 (TOBS-10).
* The .NET backend/API it consumes (OpenAPI): ADR 0004 (TOBS-4) and the stack described in ADR 0001.
* Originating design note: `docs/holdfast-architecture.md`, "Stack" (Frontend).
