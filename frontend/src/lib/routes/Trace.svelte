<script lang="ts">
  // Trace view (README 7.3), wired to GET /api/projects/{id}/traces/{traceId}. Foundation renders the span
  // waterfall and the logs on the trace; the selected-span attribute panel and correlation buttons are
  // follow-ups once issue/session correlation endpoints land.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import Panel from "../components/ui/Panel.svelte";
  import { int64, nanosToTime, severityLabel, severityClass } from "../format";

  interface Props {
    projectId: string;
    traceId: string;
  }
  let { projectId, traceId }: Props = $props();

  type Trace = components["schemas"]["TraceView"];
  type Span = components["schemas"]["IngestedSpan"];

  let trace = $state<Trace | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  const bounds = $derived.by(() => {
    const spans = trace?.spans ?? [];
    if (spans.length === 0) return { min: 0, total: 1 };
    const min = Math.min(...spans.map((s) => int64(s.startUnixNano)));
    const max = Math.max(...spans.map((s) => int64(s.endUnixNano)));
    return { min, total: Math.max(1, max - min) };
  });

  function left(s: Span): number {
    return ((int64(s.startUnixNano) - bounds.min) / bounds.total) * 100;
  }
  function width(s: Span): number {
    return Math.max(0.5, (int64(s.durationNano) / bounds.total) * 100);
  }
  function ms(nanos: number | string | undefined): string {
    return (int64(nanos) / 1_000_000).toFixed(1) + " ms";
  }

  async function load() {
    loading = true;
    error = null;
    const { data, response } = await api.GET("/api/projects/{projectId}/traces/{traceId}", {
      params: { path: { projectId, traceId } },
    });
    if (data) trace = data;
    else error = `${response.status} ${response.statusText}`;
    loading = false;
  }

  $effect(() => {
    void traceId;
    load();
  });
</script>

<header class="surface-head">
  <div>
    <p class="h">Trace</p>
    <h1 class="mono">{traceId}</h1>
  </div>
</header>

{#if loading}
  <LoadingState rows={6} />
{:else if error}
  <ErrorState message={`Could not load trace (${error}).`} onretry={load} />
{:else if trace}
  <Panel label={`Waterfall · ${trace.spans.length} spans`}>
    {#if trace.spans.length === 0}
      <EmptyState message="No spans for this trace." />
    {:else}
      {#each trace.spans as span (span.spanId)}
        <div class="span-row" class:err={int64(span.statusCode) === 2}>
          <span class="name mono" title={span.name}>{span.name}</span>
          <span class="track">
            <span class="bar" class:err={int64(span.statusCode) === 2} style="left:{left(span)}%;width:{width(span)}%"></span>
          </span>
          <span class="dur num mono muted">{ms(span.durationNano)}</span>
        </div>
      {/each}
    {/if}
  </Panel>

  <Panel label={`Logs on this trace · ${trace.logs.length}`}>
    {#if trace.logs.length === 0}
      <EmptyState message="No logs on this trace." />
    {:else}
      {#each trace.logs as log (log.id ?? log.receiptId)}
        <div class="log" class:err={severityClass(log.severityNumber) === 'lvl-err'}>
          <span class="mono muted">{nanosToTime(log.timeUnixNano)}</span>
          <span class="lvl {severityClass(log.severityNumber)}">{severityLabel(log.severityNumber)}</span>
          <span class="mono">{log.body ?? ''}</span>
        </div>
      {/each}
    {/if}
  </Panel>
{/if}

<style>
  .surface-head h1 {
    word-break: break-all;
  }
  .span-row {
    display: grid;
    grid-template-columns: 220px minmax(0, 1fr) 80px;
    gap: var(--gap-3);
    align-items: center;
    padding: 4px 0;
  }
  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .span-row.err .name {
    color: var(--err);
  }
  .track {
    position: relative;
    height: 16px;
    background: var(--table-bg);
    border-radius: var(--r-tag);
  }
  .bar {
    position: absolute;
    top: 2px;
    bottom: 2px;
    background: var(--svc-1);
    border-radius: var(--r-tag);
    min-width: 2px;
  }
  .bar.err {
    background: var(--err);
  }
  .log {
    display: grid;
    grid-template-columns: 96px 60px minmax(0, 1fr);
    gap: var(--gap-3);
    padding: 4px 0;
    border-top: 1px solid var(--divider);
  }
  .log.err {
    background: var(--err-bg);
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
