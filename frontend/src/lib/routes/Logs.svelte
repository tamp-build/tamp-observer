<script lang="ts">
  // Logs explorer (README section 9: not designed yet, same shell/patterns), wired to GET
  // /api/projects/{id}/logs over the global time window. Live tail is a follow-up.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { filters } from "../stores/filters.svelte";
  import FilterBar from "../components/shell/FilterBar.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import { nanosToTime, severityLabel, severityClass } from "../format";
  import { guard, timeout } from "../net";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  type Log = components["schemas"]["IngestedLog"];

  let logs = $state<Log[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let errorsOnly = $state(false);

  async function load() {
    loading = true;
    error = null;
    logs = [];
    const { start, end } = filters.rangeNanos;
    error = await guard("load logs", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/logs", {
        params: { path: { projectId }, query: { start, end, minSeverity: errorsOnly ? 17 : undefined, limit: 200 } },
        ...timeout(),
      });
      if (data) logs = data;
      return response;
    });
    loading = false;
  }

  $effect(() => {
    void projectId;
    void filters.window;
    void errorsOnly;
    load();
  });
</script>

<header class="surface-head">
  <h1>Logs</h1>
  <FilterBar />
</header>

<div class="toolbar">
  <label class="check">
    <input type="checkbox" bind:checked={errorsOnly} /> Errors only
  </label>
</div>

<section class="panel">
  {#if loading}
    <LoadingState rows={8} />
  {:else if error}
    <ErrorState message={`Could not load logs (${error}).`} onretry={load} />
  {:else if logs.length === 0}
    <EmptyState message="No logs in this window." />
  {:else}
    <div class="scroll-x">
      {#each logs as log, i (i)}
        <div class="log-row" class:err={severityClass(log.severityNumber) === 'lvl-err'}>
          <span class="mono muted time">{nanosToTime(log.timeUnixNano)}</span>
          <span class="lvl {severityClass(log.severityNumber)}">{severityLabel(log.severityNumber)}</span>
          <span class="mono body">{log.body ?? ''}</span>
        </div>
      {/each}
    </div>
  {/if}
</section>

<style>
  .surface-head {
    display: flex;
    align-items: flex-end;
    justify-content: space-between;
    gap: var(--gap-4);
    flex-wrap: wrap;
  }
  .toolbar {
    display: flex;
    gap: var(--gap-3);
  }
  .check {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-2);
    color: var(--text-2);
  }
  .log-row {
    display: grid;
    grid-template-columns: 96px 60px minmax(0, 1fr);
    gap: var(--gap-3);
    align-items: baseline;
    padding: 5px var(--gap-4);
    border-top: 1px solid var(--divider);
    min-width: 560px;
  }
  .log-row.err {
    background: var(--err-bg);
  }
  .body {
    white-space: pre-wrap;
    word-break: break-word;
  }
  .lvl {
    font-size: var(--fs-label);
    font-weight: 600;
  }
  .lvl-err {
    color: var(--err);
  }
  .lvl-warn {
    color: var(--warn);
  }
  .lvl-info {
    color: var(--muted);
  }
</style>
