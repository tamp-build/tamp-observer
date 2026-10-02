<script lang="ts">
  import { onMount } from "svelte";
  import type { User } from "oidc-client-ts";
  import { api, setToken } from "./lib/api/client";
  import { resolveUser, login, logout, isBundledDevAuth } from "./lib/auth";
  import type { components } from "./lib/api/schema";

  type Latency = components["schemas"]["LatencyPercentiles"];

  let user = $state<User | null>(null);
  let loading = $state(true);

  let projectId = $state("");
  let startNano = $state("0");
  let endNano = $state("9000000000000000000");
  let latency = $state<Latency | null>(null);
  let latencyStatus = $state("");

  onMount(async () => {
    user = await resolveUser();
    if (user) setToken(user.id_token ?? null);
    loading = false;
  });

  async function signIn() {
    await login();
  }

  async function signOut() {
    await logout();
    setToken(null);
    user = null;
    latency = null;
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
  <p class="sub">Svelte SPA over the .NET OpenAPI API (ADR 0014).</p>

  {#if isBundledDevAuth}
    <div class="banner">
      <strong>Development / demo mode.</strong> Signing in uses the bundled Dex dev identity provider.
      Use <code>dev@tamp.local</code> / <code>password</code>. Not for production. See
      <code>docs/deployment.md</code> to point at your own IdP.
    </div>
  {/if}

  <section>
    <h2>Session</h2>
    {#if loading}
      <p class="status">checking session...</p>
    {:else if user}
      <p>Signed in as <strong>{user.profile.email ?? user.profile.sub}</strong></p>
      <div class="row">
        <button onclick={signOut}>Sign out</button>
        <span class="status">token attached to API calls</span>
      </div>
    {:else}
      <p class="status">Not signed in. Protected <code>/api</code> routes require a token.</p>
      <button onclick={signIn}>Sign in</button>
    {/if}
  </section>

  <section>
    <h2>Latency percentiles</h2>
    <label>Project id <input bind:value={projectId} placeholder="GUID" /></label>
    <label>Start (unix ns) <input bind:value={startNano} /></label>
    <label>End (unix ns) <input bind:value={endNano} /></label>
    <div class="row">
      <button onclick={queryLatency} disabled={!projectId || !user}>Query</button>
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
  .banner {
    background: #2a2410;
    border: 1px solid #5c4d16;
    color: #e7d8a6;
    border-radius: 8px;
    padding: 0.75rem 1rem;
    font-size: 0.9rem;
    margin-top: 1rem;
  }
  .banner code {
    background: #11151c;
    padding: 0.05rem 0.3rem;
    border-radius: 4px;
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
  table {
    margin-top: 0.75rem;
    background: #11151c;
    border-radius: 6px;
    padding: 0.5rem 0.75rem;
  }
  th {
    text-align: left;
    padding-right: 1rem;
    color: #9aa4b2;
    font-weight: 500;
  }
  code {
    font-size: 0.85em;
  }
</style>
