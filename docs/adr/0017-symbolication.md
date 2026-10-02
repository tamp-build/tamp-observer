# ADR 0017: Symbolication (JS source maps first)

* Status: Proposed
* Date: 2026-10-02
* Deciders: scott
* Tracking: TOBS-18

## Context and Problem Statement

Minified JavaScript stack traces are gibberish: `a.min.js:1:4823 in t`. Without symbolication an Issue's
stack trace is unreadable and its fingerprint (ADR 0015) is coarse. We need to turn minified/compiled
frames back into original source using artifacts the build produces (source maps, PDBs, native symbols).

This ADR decides what we symbolicate first, how artifacts are stored, and where resolution lives.

## Decision Drivers

* **Biggest, most self-contained win first.** Minified JS is the clear pain, and Source Map v3 is a
  precise, language-agnostic spec. .NET usually already ships readable text stack traces, and native needs
  platform debug symbols; both are later.
* **Right map per release.** A source map only matches the exact bundle it was generated for, so artifacts
  must be scoped by version.
* **Resolution is pure and testable.** Frame lookup should not require infrastructure to exercise.
* **No heavy dependency for a well-specified format.** The v3 VLQ mapping decode is small and stable.

## Decision

### 1. Source maps first

The first symbolicator consumes **JavaScript Source Map v3**. .NET PDB and native symbol support are
future additions behind the same interface.

### 2. Artifacts are uploaded and stored, scoped by version

A `SymbolArtifact` holds a source map, keyed by `(ProjectId, ServiceId, VersionId, GeneratedFile)` (e.g.
`app.min.js`). Uploading stores it; the Version scope (ADR 0007/0008) guarantees the matching map for a
release. Baseline storage is a Marten document with the map content inline; very large maps can move to
blob storage later (as session replay does, ADR 0010).

### 3. Resolution is a storage-agnostic frame lookup

`ISymbolicator.Symbolicate(project, service, version, MinifiedFrame) -> OriginalFrame?` resolves a
generated `(line, column)` to the original `(source, line, column, name)`. The source-map decode (base64
VLQ mappings per the v3 spec) is implemented in-house, no external package. The resolver depends only on an
`ISymbolArtifactStore` abstraction, so it is unit-tested with an in-memory store (fast lane); a Marten store
backs it in production.

### 4. Where it runs

Symbolication is evaluator/store processing (ADR 0009). It integrates into the exception path to produce
readable frames and to refine the Issue fingerprint (ADR 0015) once exception stack traces are parsed from
OTLP attributes. This ADR delivers the core (store + resolve); that integration is the follow-on.

## Consequences

### Positive

* Readable stack traces and a sharper fingerprint for the JS archetype, the common minified case.
* The resolver is pure and fast to test; no infra, no third-party decoder to audit (good for accreditation).
* Version-scoped artifacts mean a frame is always resolved against the bundle it came from.

### Negative / accepted costs

* **An in-house VLQ decoder to maintain.** Accepted: the v3 format is small, stable, and well-specified,
  and it keeps the dependency/accreditation surface minimal.
* **Artifact upload/retention is operator work.** Accepted; scoping by version keeps it tractable.

### Neutral / future-facing

* .NET PDB and native symbol symbolicators are future providers of the same interface.
* Inline map content in Marten is fine at first; a blob tier is the escape hatch for very large maps.
* Integration into the exception/Issue path waits on parsing exception stack traces from OTLP attributes
  (ADR 0009 / ADR 0015), which the trimmed OTLP schema does not do yet.

## Links

* Fingerprint this refines: ADR 0015 (TOBS-16).
* Attribute-carried exception data and evaluator reconstruction: ADR 0009 (TOBS-9).
* Version scoping of artifacts: ADR 0007 / ADR 0008.
* Blob-vs-inline storage precedent: ADR 0010 (session replay).
* Originating design note: `docs/holdfast-architecture.md`, "App feature inventory" (Symbolication).
