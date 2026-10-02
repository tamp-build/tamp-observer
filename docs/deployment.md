# Deployment

tamp-observer ships a single-host Docker Compose stack that scales by dialing tiers up, not by rewriting the
deployment (ADR 0001 floor-first; ADR 0005 storage tiering; ADR 0004 raw-bucket tier). The floor is `.NET +
Postgres + the Go collector`; every higher tier is additive and independently omittable.

## Quick start (the floor)

```
docker compose up --build
docker compose run --rm evaluator create-project my-app    # create a trust-root Project (ADR 0007)
```

Then point your app's OTLP exporter at `localhost:4317` (gRPC) or `:4318` (HTTP) and open `http://localhost:8080`.
Telemetry must carry a `tamp.project.key` resource attribute matching the Project key, or it is quarantined,
not admitted (ADR 0004/0007).

The floor is Postgres-only: no Valkey, no ClickHouse. The collector lands OTLP to a file spool, the evaluator
drains it to Postgres, and the API serves both the read interface and the built Svelte SPA (ADR 0014) from one
container.

## Variants (overlays)

Each variant is a compose overlay layered on the floor; they are orthogonal and stack. The raw-bucket tier
(how raw bytes are buffered before admit) and the storage tier (where telemetry is written/read) dial
independently.

| Variant          | Command (added to `-f docker-compose.yml`)      | Raw-bucket tier | Telemetry storage                         | Extra container |
|------------------|-------------------------------------------------|-----------------|-------------------------------------------|-----------------|
| Floor (default)  | (none)                                          | file spool      | Postgres                                  | none            |
| Valkey buffer    | `-f deploy/compose.valkey.yml`                  | Valkey stream   | Postgres                                  | `valkey`        |
| ClickHouse tier  | `-f deploy/compose.clickhouse.yml`              | file spool      | ClickHouse (entities stay in Postgres)    | `clickhouse`    |
| DuckDB tier      | `-f deploy/compose.duckdb.yml`                  | file spool      | DuckDB reads Postgres (in-process)        | none            |

Overlays stack, for example the Valkey buffer with the ClickHouse tier:

```
docker compose -f docker-compose.yml -f deploy/compose.valkey.yml -f deploy/compose.clickhouse.yml up --build
```

### What each tier is

* **Valkey buffer** (ADR 0004): the raw-bucket high tier. The collector `XADD`s raw OTLP to a Valkey stream;
  the evaluator drains it with a consumer group. Valkey is a transient buffer (persistence off), not storage.
* **ClickHouse tier** (ADR 0005): the analytical telemetry tier. Spans and logs are written to and read from
  ClickHouse; entities, issues, auth, and symbols stay in Postgres (the system of record). The evaluator uses
  a composite sink to fan the one admitted batch across both engines.
* **DuckDB tier** (ADR 0005): an in-process read accelerator, not a service. The API spins an embedded DuckDB
  per query, attaches the same Postgres via DuckDB's postgres extension, runs the columnar aggregation there,
  and goes idle. DuckDB downloads that extension on first use (needs network); an air-gapped install
  pre-bundles it.

## Configuration dials (env)

The overlays set these; you can also set them directly.

| Variable            | Host      | Values                          | Default    | Meaning                                              |
|---------------------|-----------|---------------------------------|------------|------------------------------------------------------|
| `OBSERVER_RAWBUCKET`| evaluator | `file` \| `valkey`              | `file`     | Raw-bucket tier the evaluator drains (ADR 0004).     |
| `OBSERVER_SINK`     | evaluator | `postgres` \| `clickhouse`      | `postgres` | Where admitted telemetry is written (ADR 0006).      |
| `OBSERVER_STORE`    | api       | `postgres` \| `duckdb` \| `clickhouse` | `postgres` | Which engine answers read queries (ADR 0006).  |
| `OBSERVER_CLICKHOUSE`| both     | connection string              | (unset)    | Required when sink/store is `clickhouse`.            |
| `OBSERVER_DB`       | both      | connection string              | local dev  | The Postgres system of record.                       |

Absent env selects the Postgres floor. Unknown values, or `clickhouse` without `OBSERVER_CLICKHOUSE`, fail
loud at startup rather than silently falling back.

## Verifying a variant

The API host has a diagnostic that runs one read through whichever store the dial selected, so you can confirm
a variant works in your environment without an IdP token:

```
docker compose -f docker-compose.yml -f deploy/compose.duckdb.yml run --rm api selftest-store
```

It prints the selected engine and a span count (for DuckDB this exercises the postgres-extension path).

## Authentication

Authentication is always external (ADR 0013); there is no local credential store. Set `OBSERVER_OIDC_AUTHORITY`
and `OBSERVER_OIDC_AUDIENCE` on the API to point at your IdP (GitHub OIDC for connected dev, a definable
in-enclave OIDC such as Keycloak for air-gapped). The SPA, `/health`, and `/openapi` are anonymous; every
`/api` route requires a token. A laptop with no IdP can load the UI but cannot reach `/api`.
