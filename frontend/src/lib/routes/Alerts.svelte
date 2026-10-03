<script lang="ts">
  // Alerts (README section 9: not designed yet), wired to GET /api/projects/{id}/alerts. The built-in
  // new-issue / regressed-issue rules show here; the rule builder and live feed are follow-ups.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import Panel from "../components/ui/Panel.svelte";
  import { timeAgo } from "../format";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  type Alerts = components["schemas"]["AlertsView"];

  let data = $state<Alerts | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function load() {
    loading = true;
    error = null;
    const res = await api.GET("/api/projects/{projectId}/alerts", { params: { path: { projectId } } });
    if (res.data) data = res.data;
    else error = `${res.response.status} ${res.response.statusText}`;
    loading = false;
  }

  $effect(() => {
    void projectId;
    load();
  });
</script>

<header class="surface-head"><h1>Alerts</h1></header>

{#if loading}
  <LoadingState rows={4} />
{:else if error}
  <ErrorState message={`Could not load alerts (${error}).`} onretry={load} />
{:else if data}
  <Panel label="Rules">
    {#if data.rules.length === 0}
      <EmptyState message="No alert rules configured." />
    {:else}
      {#each data.rules as rule (rule.id)}
        <div class="rule">
          <span>{rule.name}</span>
          <span class="pill {rule.enabled ? 'res' : 'muted-pill'}">{rule.enabled ? "Enabled" : "Off"}</span>
        </div>
      {/each}
    {/if}
  </Panel>

  <Panel label="Recent alerts">
    {#if data.history.length === 0}
      <EmptyState message="No alerts fired in this window." />
    {:else}
      {#each data.history as ev (ev.issueId + ev.atUtc)}
        <a class="event" href={router.projectHref(projectId, `/issues/${ev.issueId}`)} use:link data-keep-filters="true">
          <span class="tag">{ev.kind}</span>
          <span class="title">{ev.title}</span>
          <span class="muted">{timeAgo(ev.atUtc)}</span>
        </a>
      {/each}
    {/if}
  </Panel>
{/if}

<style>
  .surface-head h1 {
    margin: 0;
  }
  .rule {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--gap-2) 0;
    border-top: 1px solid var(--divider);
  }
  .rule:first-child {
    border-top: 0;
  }
  .event {
    display: grid;
    grid-template-columns: 120px minmax(0, 1fr) 90px;
    gap: var(--gap-3);
    align-items: center;
    padding: var(--gap-2) 0;
    border-top: 1px solid var(--divider);
    color: var(--text);
  }
  .event:hover {
    background: var(--surface-hover);
  }
  .title {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
