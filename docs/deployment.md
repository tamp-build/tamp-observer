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

### Session replay (rrweb)

The SPA records its own session with rrweb and ships it to the replay front door, keyed to the project
`spa-demo` by default (`VITE_REPLAY_PROJECT_KEY`). Create that project so the demo has somewhere to land, then
open the app, sign in, and use the **Session replay** panel to list and play sessions:

```
docker compose run --rm evaluator create-project spa-demo "SPA Demo"
```

Replay metadata lives in Postgres; the DOM-event firehose is stored as a blob under `OBSERVER_REPLAY_BLOB`
(a filesystem path by default). Masking is client-side (inputs masked by default, ADR 0010); consent and
smart-capture (ADR 0011) are not wired yet.

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

Authentication is **always external** (ADR 0013): tamp-observer never stores passwords. It validates OIDC
tokens issued by an identity provider. The SPA, `/health`, and `/openapi` are anonymous; every `/api` route
requires a valid token, checked through the single RBAC chokepoint.

### The bundled development / demo identity provider (what the floor ships)

> **This is a development and demo convenience, and is labelled as such in the UI. Do not use it in
> production.** The floor `docker compose` bundles a small OIDC provider, [Dex](https://dexidp.io/), purely so
> you can click around on a single box without first standing up an IdP. The application itself does not depend
> on Dex; it only speaks standard OIDC, the same code path it uses against a real provider.

What that means in practice, for a dev or an IT admin kicking the tires:

* `docker compose up --build`, open `http://localhost:8080`, click **Sign in**. The UI shows a yellow
  "Development / demo mode" banner so nobody mistakes it for real auth.
* Log in at the Dex page with the preconfigured dev user: **`dev@tamp.local` / `password`**.
* You are redirected back signed in; the SPA attaches your token to every `/api` call automatically (browser
  authorization-code + PKCE flow, public client `tamp-observer`).
* Prefer scripting? Grab a token directly with the password grant:

  ```
  TOKEN=$(curl -s -X POST http://localhost:5556/dex/token \
    -d grant_type=password -d client_id=tamp-observer -d scope="openid profile email" \
    -d username=dev@tamp.local -d password=password | jq -r .id_token)
  curl -H "Authorization: Bearer $TOKEN" http://localhost:8080/api/me
  ```

The dev user, the client, and the fact that it is HTTP with in-memory state all live in `deploy/dex/config.yaml`.
Edit it to add users or clients for local experimentation.

One implementation detail worth knowing if you tinker: the issuer is `http://id.localhost:5556/dex`. The host
`id.localhost` is deliberate so the token issuer string resolves to the *same* Dex from two places, the browser
(browsers resolve `*.localhost` to loopback automatically) and the API container (reaches it via a
`host-gateway` entry in `docker-compose.yml`). That sidesteps the usual OIDC-in-Docker issuer-mismatch trap.

### Using your own identity provider (production, connected dev, or air-gapped)

Point the API at your IdP and drop the bundled one:

* Set `OBSERVER_OIDC_AUTHORITY` to your issuer URL and `OBSERVER_OIDC_AUDIENCE` to the client/audience your
  tokens carry. Remove `OBSERVER_OIDC_ALLOW_HTTP` (leave HTTPS metadata required).
* For the SPA, set `VITE_OIDC_AUTHORITY` and `VITE_OIDC_CLIENT_ID` at build time to match.
* Remove the `dex` service from the compose (and its CORS `allowedOrigins` concern disappears with it).

Examples: GitHub OIDC for connected dev; a definable in-enclave OIDC such as Keycloak or AD FS for air-gapped
or accredited sites (ADR 0013 treats in-enclave OIDC as load-bearing for the air-gapped ceiling). The bundled
Dex is also the working reference for how that wiring looks.
