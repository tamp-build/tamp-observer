<script lang="ts">
  import { onMount } from "svelte";
  import type { User } from "oidc-client-ts";
  import { api, setToken } from "./lib/api/client";
  import { initAuth, resolveUser, login, logout, isBundledDevAuth } from "./lib/auth";
  import { startRecording, type Recording } from "./lib/replay/recorder";
  import ReplayPlayer from "./lib/replay/ReplayPlayer.svelte";
  import type { components } from "./lib/api/schema";

  type Latency = components["schemas"]["LatencyPercentiles"];
  type SessionSummary = components["schemas"]["ReplaySessionSummary"];

  let user = $state<User | null>(null);
  let loading = $state(true);
  let devMode = $state(false);
  let recording = $state<Recording | null>(null);

  let projectId = $state("");

  let latency = $state<Latency | null>(null);
  let latencyStatus = $state("");

  let sessions = $state<SessionSummary[]>([]);
  let sessionsStatus = $state("");
  let activeEvents = $state<unknown[] | null>(null);
  let activeSession = $state<string | null>(null);

  onMount(async () => {
    await initAuth();
    devMode = isBundledDevAuth();
    user = await resolveUser();
    if (user) {
      setToken(user.id_token ?? null);
      sessionStorage.removeItem("tobs_login_bounced");
    } else if (!sessionStorage.getItem("tobs_login_bounced")) {
      // Land straight on the IdP sign-in (GitHub via Dex): auto-redirect once per session. The guard keeps a
      // cancelled login from looping; after a bounce the signed-out UI with a manual Sign in button is shown.
      sessionStorage.setItem("tobs_login_bounced", "1");
      await login();
      return;
    }
    loading = false;
    // Record this page as a replay session only once signed in (dogfoods the capture path, ADR 0010).
    if (user) recording = startRecording();
  });

  async function signIn() {
    await login();
  }

  async function signOut() {
    await logout();
    setToken(null);
    user = null;
    latency = null;
    sessions = [];
    activeEvents = null;
  }

  async function queryLatency() {
    latency = null;
    latencyStatus = "querying...";
    const { data, response } = await api.GET("/api/projects/{projectId}/latency", {
      params: { path: { projectId }, query: { start: 0, end: 9000000000000000000 } },
    });
    if (data) {
      latency = data;
      latencyStatus = "ok";
    } else {
      latencyStatus = `${response.status} ${response.statusText}`;
    }
  }

  async function loadSessions() {
    sessions = [];
    activeEvents = null;
    sessionsStatus = "loading...";
    const { data, response } = await api.GET("/api/projects/{projectId}/sessions", {
      params: { path: { projectId } },
    });
    if (data) {
      sessions = data;
      sessionsStatus = `${data.length} session(s)`;
    } else {
      sessionsStatus = `${response.status} ${response.statusText}`;
    }
  }

  async function playSession(sessionId: string) {
    activeEvents = null;
    activeSession = sessionId;
    const { data, response } = await api.GET("/api/projects/{projectId}/sessions/{sessionId}/events", {
      params: { path: { projectId, sessionId } },
      parseAs: "json",
    });
    if (response.ok) {
      activeEvents = (data as unknown as unknown[]) ?? [];
    } else {
      sessionsStatus = `events: ${response.status} ${response.statusText}`;
    }
  }
</script>

<main>
  <h1>tamp-observer</h1>
  <p class="sub">Svelte SPA over the .NET OpenAPI API (ADR 0014). This page is recording itself for replay.</p>

  {#if devMode}
    <div class="banner">
      <strong>Development / demo mode.</strong> Sign-in uses the bundled Dex dev identity provider
      (<code>dev@tamp.local</code> / <code>password</code>). Not for production. See <code>docs/deployment.md</code>.
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
        {#if recording}<span class="status">recording session {recording.sessionId.slice(0, 8)}...</span>{/if}
      </div>
    {:else}
      <p class="status">Not signed in. Protected <code>/api</code> routes require a token.</p>
      <button onclick={signIn}>Sign in</button>
    {/if}
  </section>

  <section>
    <h2>Project</h2>
    <label>Project id <input bind:value={projectId} placeholder="GUID" /></label>
  </section>

  <section>
    <h2>Session replay</h2>
    <div class="row">
      <button onclick={loadSessions} disabled={!projectId || !user}>Load sessions</button>
      <span class="status">{sessionsStatus}</span>
    </div>
    {#if sessions.length}
      <table>
        <thead><tr><th>session</th><th>events</th><th>started</th><th></th></tr></thead>
        <tbody>
          {#each sessions as s (s.sessionId)}
            <tr>
              <td>{s.sessionId.slice(0, 8)}...</td>
              <td>{s.eventCount}</td>
              <td>{new Date(s.startedAtUtc).toLocaleTimeString()}</td>
              <td><button onclick={() => playSession(s.sessionId)}>Replay</button></td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
    {#if activeEvents}
      <h3>Replay {activeSession?.slice(0, 8)}...</h3>
      <ReplayPlayer events={activeEvents} />
    {/if}
  </section>

  <section>
    <h2>Latency percentiles</h2>
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
    max-width: 52rem;
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
    width: 100%;
    border-collapse: collapse;
    background: #11151c;
    border-radius: 6px;
  }
  th,
  td {
    text-align: left;
    padding: 0.35rem 0.6rem;
    color: #c6cedb;
    font-size: 0.9rem;
  }
  th {
    color: #9aa4b2;
    font-weight: 500;
  }
  code {
    font-size: 0.85em;
  }
</style>
