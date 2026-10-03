<script lang="ts">
  // Project overview (README section 9: not designed yet). Foundation shows the two read capabilities already
  // built: latency percentiles and top operations over the global window.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { instance } from "../stores/instance.svelte";
  import { filters } from "../stores/filters.svelte";
  import FilterBar from "../components/shell/FilterBar.svelte";
  import Panel from "../components/ui/Panel.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import { int64 } from "../format";
  import { guard, timeout } from "../net";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  type Latency = components["schemas"]["LatencyPercentiles"];
  type Op = components["schemas"]["OperationStat"];

  let latency = $state<Latency | null>(null);
  let ops = $state<Op[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  const projectName = $derived(instance.project(projectId)?.name ?? "Project");

  function ms(nanos: number | string): string {
    return (int64(nanos) / 1_000_000).toFixed(1) + " ms";
  }

  async function load() {
    loading = true;
    error = null;
    const { start, end } = filters.rangeNanos;
    error = await guard("load overview", async () => {
      const [lat, op] = await Promise.all([
        api.GET("/api/projects/{projectId}/latency", { params: { path: { projectId }, query: { start, end } }, ...timeout() }),
        api.GET("/api/projects/{projectId}/operations", { params: { path: { projectId }, query: { start, end, limit: 10 } }, ...timeout() }),
      ]);
      latency = lat.data ?? null;
      ops = op.data ?? [];
      // Surface the first failing response, if any.
      return lat.response.ok ? op.response : lat.response;
    });
    loading = false;
  }

  $effect(() => {
    void projectId;
    void filters.window;
    load();
  });
</script>

<header class="surface-head">
  <div>
    <p class="h">{projectName}</p>
    <h1>Overview</h1>
  </div>
  <FilterBar />
</header>

{#if loading}
  <LoadingState rows={4} />
{:else if error}
  <ErrorState message={`Could not load overview (${error}).`} onretry={load} />
{:else}
  <Panel label="Latency">
    {#if latency && int64(latency.count) > 0}
      <div class="stats">
        <div class="stat"><span class="v mono">{int64(latency.count).toLocaleString()}</span><span class="k muted">spans</span></div>
        <div class="stat"><span class="v mono">{ms(latency.p50)}</span><span class="k muted">p50</span></div>
        <div class="stat"><span class="v mono">{ms(latency.p95)}</span><span class="k muted">p95</span></div>
        <div class="stat"><span class="v mono">{ms(latency.p99)}</span><span class="k muted">p99</span></div>
      </div>
    {:else}
      <EmptyState message="No spans in this window." />
    {/if}
  </Panel>

  <Panel label="Top operations">
    {#if ops.length === 0}
      <EmptyState message="No operations in this window." />
    {:else}
      <div class="tr th"><span>Operation</span><span class="num">Calls</span><span class="num">Errors</span></div>
      {#each ops as op (op.operation)}
        <div class="tr">
          <span class="mono">{op.operation}</span>
          <span class="num mono">{int64(op.count).toLocaleString()}</span>
          <span class="num mono" class:err={int64(op.errorCount) > 0}>{int64(op.errorCount).toLocaleString()}</span>
        </div>
      {/each}
    {/if}
  </Panel>
{/if}

<style>
  .surface-head {
    display: flex;
    align-items: flex-end;
    justify-content: space-between;
    gap: var(--gap-4);
    flex-wrap: wrap;
  }
  .stats {
    display: flex;
    gap: var(--gap-5);
    flex-wrap: wrap;
  }
  .stat {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .stat .v {
    font-size: 20px;
    color: var(--text);
  }
  .stat .k {
    font-size: var(--fs-label);
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }
  .tr {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 90px 90px;
    gap: var(--gap-3);
    padding: 6px 0;
    border-top: 1px solid var(--divider);
  }
  .th {
    font-size: var(--fs-label);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
    font-weight: 600;
    border-top: 0;
  }
  .err {
    color: var(--err);
  }
</style>
