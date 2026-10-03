<script lang="ts">
  // Session replay (README 7.4). Foundation: the session list (a gap the backend already serves via
  // /sessions) and the rrweb player for a chosen session (/sessions/{id}/events). The custom timeline,
  // correlation overlay and error-stepping are follow-ups; the basic rrweb controller stands in for now.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import ReplayPlayer from "../replay/ReplayPlayer.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import Icon from "../components/ui/Icon.svelte";
  import { int64, timeAgo } from "../format";
  import { guard, timeout } from "../net";

  interface Props {
    projectId: string;
    sessionId?: string;
  }
  let { projectId, sessionId }: Props = $props();

  type Session = components["schemas"]["ReplaySessionSummary"];

  let sessions = $state<Session[]>([]);
  let events = $state<unknown[] | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function loadList() {
    loading = true;
    error = null;
    error = await guard("load sessions", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/sessions", {
        params: { path: { projectId } },
        ...timeout(),
      });
      if (data) sessions = data;
      return response;
    });
    loading = false;
  }

  async function loadSession(id: string) {
    loading = true;
    error = null;
    events = null;
    error = await guard("load session events", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/sessions/{sessionId}/events", {
        params: { path: { projectId, sessionId: id } },
        parseAs: "json",
        ...timeout(),
      });
      if (response.ok) events = (data as unknown as unknown[]) ?? [];
      return response;
    });
    loading = false;
  }

  $effect(() => {
    if (sessionId) loadSession(sessionId);
    else loadList();
  });
</script>

{#if sessionId}
  <nav class="crumbs mono muted">
    <a href={router.projectHref(projectId, "/replay")} use:link data-keep-filters="true">Session replay</a>
    <Icon name="chevron-right" size={12} />
    <span>{sessionId}</span>
  </nav>
  <section class="panel player">
    {#if loading}
      <LoadingState rows={3} />
    {:else if error}
      <ErrorState message={`Could not load session (${error}).`} onretry={() => loadSession(sessionId)} />
    {:else if events}
      <div class="overlay-note muted">Reconstructed DOM, not video. Masking as captured.</div>
      <ReplayPlayer {events} />
    {/if}
  </section>
{:else}
  <header class="surface-head"><h1>Session replay</h1></header>
  <section class="panel">
    {#if loading}
      <LoadingState rows={6} />
    {:else if error}
      <ErrorState message={`Could not load sessions (${error}).`} onretry={loadList} />
    {:else if sessions.length === 0}
      <EmptyState message="No replay sessions captured yet." />
    {:else}
      <div class="tr th"><span>Session</span><span class="num">Events</span><span>Started</span><span>Browser</span></div>
      {#each sessions as s (s.sessionId)}
        <a class="tr row" href={router.projectHref(projectId, `/replay/${encodeURIComponent(s.sessionId)}`)} use:link data-keep-filters="true">
          <span class="mono">{s.sessionId}</span>
          <span class="num mono">{int64(s.eventCount).toLocaleString()}</span>
          <span class="muted">{timeAgo(s.startedAtUtc)}</span>
          <span class="muted ua">{s.userAgent ?? "-"}</span>
        </a>
      {/each}
    {/if}
  </section>
{/if}

<style>
  .crumbs {
    display: flex;
    align-items: center;
    gap: var(--gap-1);
  }
  .surface-head h1 {
    margin: 0;
  }
  .player {
    padding: var(--gap-3);
    display: flex;
    flex-direction: column;
    gap: var(--gap-2);
  }
  .overlay-note {
    font-size: var(--fs-label);
  }
  .tr {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 80px 110px 160px;
    gap: var(--gap-3);
    align-items: center;
    padding: 8px var(--gap-4);
    min-width: 560px;
  }
  .tr.row {
    border-top: 1px solid var(--divider);
    color: var(--text);
  }
  .tr.row:hover {
    background: var(--surface-hover);
  }
  .th {
    font-size: var(--fs-label);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
    font-weight: 600;
  }
  .ua {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
