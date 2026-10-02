<script lang="ts">
  import { api, setToken, hasToken } from "./lib/api/client";
  import type { components } from "./lib/api/schema";

  type Me = components["schemas"]["MeResponse"];
  type Latency = components["schemas"]["LatencyPercentiles"];

  // External-IdP bearer token (ADR 0013). Pasted here for the scaffold; a real sign-in flow replaces this.
  let token = $state("");

  let me = $state<Me | null>(null);
  let meStatus = $state<string>("not checked");

  let projectId = $state("");
  let startNano = $state("0");
  let endNano = $state("1000000000000000000");
  let latency = $state<Latency | null>(null);
  let latencyStatus = $state<string>("");

  function applyToken() {
    setToken(token);
    me = null;
    meStatus = "not checked";
  }

  async function checkMe() {
    meStatus = "checking...";
    const { data, error, response } = await api.GET("/api/me");
    if (data) {
      me = data;
      meStatus = `authenticated as ${data.subjectId}`;
    } else {
      me = null;
      meStatus = `${response.status} ${response.statusText}${error ? "" : ""}`;
    }
  }

  async function queryLatency() {
    latency = null;
    latencyStatus = "querying...";
    const { data, response } = await api.GET("/api/projects/{projectId}/latency", {
      params: {
        path: { projectId },
        query: { start: Number(startNano), end: Number(endNano) },
      },
    });
    if (data) {
      latency = data;
      latencyStatus = "ok";
    } else {
      latencyStatus = `${response.status} ${response.statusText}`;
    }
  }
</script>

<main>
  <h1>tamp-observer</h1>
  <p class="sub">Svelte SPA over the .NET OpenAPI API (ADR 0014). Typed client generated from the backend contract.</p>

  <section>
    <h2>Session</h2>
    <label>
      Bearer token (from your OIDC provider)
      <input type="password" bind:value={token} placeholder="paste access token" />
    </label>
    <div class="row">
      <button onclick={applyToken}>Apply token</button>
      <button onclick={checkMe}>Check /api/me</button>
      <span class="status">{hasToken() ? "token set" : "no token"} &middot; {meStatus}</span>
    </div>
    {#if me}
      <pre>{JSON.stringify(me, null, 2)}</pre>
    {/if}
  </section>

  <section>
    <h2>Latency percentiles</h2>
    <label>Project id <input bind:value={projectId} placeholder="GUID" /></label>
    <label>Start (unix ns) <input bind:value={startNano} /></label>
    <label>End (unix ns) <input bind:value={endNano} /></label>
    <div class="row">
      <button onclick={queryLatency} disabled={!projectId}>Query</button>
      <span class="status">{latencyStatus}</span>
    </div>
    {#if latency}
      <table>
        <tbody>
          <tr><th>count</th><td>{latency.count}</td></tr>
          <tr><th>p50</th><td>{latency.p50}</td></tr>
          <tr><th>p95</th><td>{latency.p95}</td></tr>
          <tr><th>p99</th><td>{latency.p99}</td></tr>
        </tbody>
      </table>
    {/if}
  </section>
</main>

<style>
  :global(body) {
    margin: 0;
    font-family: system-ui, sans-serif;
    background: #0f1115;
    color: #e6e6e6;
  }
  main {
    max-width: 48rem;
    margin: 0 auto;
    padding: 1.5rem 1rem 4rem;
  }
  h1 {
    margin: 0 0 0.25rem;
  }
  .sub {
    color: #9aa4b2;
    margin-top: 0;
  }
  section {
    border: 1px solid #262b36;
    border-radius: 8px;
    padding: 1rem;
    margin-top: 1.25rem;
  }
  h2 {
    margin-top: 0;
    font-size: 1.1rem;
  }
  label {
    display: block;
    margin: 0.5rem 0;
    font-size: 0.9rem;
    color: #c6cedb;
  }
  input {
    display: block;
    width: 100%;
    box-sizing: border-box;
    margin-top: 0.25rem;
    padding: 0.4rem 0.5rem;
    background: #171b22;
    border: 1px solid #2c3340;
    border-radius: 6px;
    color: #e6e6e6;
  }
  .row {
    display: flex;
    gap: 0.75rem;
    align-items: center;
    margin-top: 0.75rem;
    flex-wrap: wrap;
  }
  button {
    padding: 0.4rem 0.9rem;
    background: #2563eb;
    color: white;
    border: none;
    border-radius: 6px;
    cursor: pointer;
  }
  button:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
  .status {
    color: #9aa4b2;
    font-size: 0.85rem;
  }
  pre,
  table {
    margin-top: 0.75rem;
    background: #11151c;
    border-radius: 6px;
    padding: 0.5rem 0.75rem;
    overflow: auto;
  }
  th {
    text-align: left;
    padding-right: 1rem;
    color: #9aa4b2;
    font-weight: 500;
  }
</style>
