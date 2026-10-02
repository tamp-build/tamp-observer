# tamp-observer

Self-hosted, in-house observability for the [tamp](https://github.com/tamp-build) ecosystem: error
monitoring, session replay, logging, tracing, and metrics, for single-tenant deployments that keep
everything in-house.

tamp-observer is the observability pillar of tamp, alongside **tamp** (attestation/build) and
**tamp.findings** (security/gating). It does what highlight.io and Sentry do, under constraints those
tools do not meet: it runs from one `docker compose up` on a dev shop's laptop up to an air-gapped,
ITAR-restricted enclave, with no cloud dependency and no runtime call-home.

> Status: early. The architecture is recorded; implementation is starting fresh. Internal codename:
> Holdfast.

## Positioning: floor to ceiling

* **Floor (the default):** one `docker compose up`, Postgres only, simplest auth, sane capture
  defaults. Value in minutes, with no need to read about tiers, air-gap, or ITAR.
* **Ceiling:** air-gapped / regulated enclaves with in-enclave auth, native RBAC, strict privacy, and a
  non-negotiable enforcement posture.

Every higher tier (buffer tier, ClickHouse, in-enclave OIDC, locked enforcement) is additive and
independently omittable. The floor runs on nothing but .NET, Postgres, and the Go collector.

## Shape, in one paragraph

Agents send OTLP to a custom Go OpenTelemetry Collector that terminates the protocol and lands raw
bytes fast, with no domain logic. A .NET evaluator consumes that stream, resolves entities
(Project / Service / Environment / Version), adjudicates, and promotes events into a tiered store
(Postgres/Marten baseline, DuckDB on demand, ClickHouse opt-in). A Svelte frontend reads a typed
OpenAPI client. Session replay uses rrweb through a dedicated front door.

## Documentation

* [Architecture and design handoff](docs/holdfast-architecture.md): the 5000-foot design notes.
* [Architecture Decision Records](docs/adr/README.md): the pinned decisions, one per file, in MADR
  format, matching the tamp house style.

## Building and testing

The build dogfoods the tamp ecosystem: tamp drives its own restore / build / test flow through
`build/Build.cs`. You need the .NET 10 SDK (pinned in `global.json`); integration tests additionally
need Docker (they spin ephemeral Postgres / ClickHouse / Valkey via Testcontainers).

```
dotnet run --project build -- Compile          # restore + build the solution
dotnet run --project build -- UnitTest         # fast: unit tests only, no containers
dotnet run --project build -- IntegrationTest  # Testcontainers suites (Docker required)
dotnet run --project build -- Test             # everything (unit + integration)
dotnet run --project build -- Ci               # info + clean + unit tests (the PR lane)
```

CI mirrors this split: **`ci.yml`** runs build + unit tests on every push and PR (no Docker);
**`nightly.yml`** runs the integration suites plus the SonarCloud scan with C#/Go coverage on a
nightly schedule. Integration tests are tagged `[Trait("Category", "Integration")]`.

For a local Postgres to run against directly, `docker compose up` brings up the floor tier (Postgres
only) on `localhost:5432` (`observer` / `observer`).

### Layout

```
src/   Tamp.Observer.Domain            entity model (ADR 0007/0008)
       Tamp.Observer.Storage.Postgres  Marten baseline store (ADR 0005/0006)
tests/ Tamp.Observer.Storage.Postgres.Tests
build/ Build.cs                        tamp self-hosted build
docs/  architecture + ADRs
```

## License

[MIT](LICENSE), matching the rest of the tamp ecosystem.
