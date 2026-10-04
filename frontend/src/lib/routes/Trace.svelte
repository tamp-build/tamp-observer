<script lang="ts">
  // Trace view (README 7.3 / TOBS-28): header with root op + metadata + replay link, a service-colored depth
  // waterfall with a time axis, a selected-span attribute panel (exception.issue / session links), logs on the
  // trace, and the reusable correlation walk. Wired to GET /traces/{id} (spans + logs incl. attributes).
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import { services } from "../stores/services.svelte";
  import { entities } from "../stores/entities.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import Panel from "../components/ui/Panel.svelte";
  import Icon from "../components/ui/Icon.svelte";
  import CorrelationWalk from "../components/CorrelationWalk.svelte";
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
  let selectedId = $state<string | null>(null);
  // Resolved Issue id for the selected error span's fingerprint (TOBS-28), for the exception->issue link.
  let selectedIssueId = $state<string | null>(null);

  const SVC_COLORS = ["var(--svc-1)", "var(--svc-2)", "var(--svc-3)", "#B7A0FF", "#7AD0A8"];

  const spans = $derived(trace?.spans ?? []);
  const bounds = $derived.by(() => {
    if (spans.length === 0) return { min: 0, total: 1 };
    const min = Math.min(...spans.map((s) => int64(s.startUnixNano)));
    const max = Math.max(...spans.map((s) => int64(s.endUnixNano)));
    return { min, total: Math.max(1, max - min) };
  });
  const serviceColor = $derived.by(() => {
    const ids = [...new Set(spans.map((s) => s.serviceId ?? ""))];
    const m: Record<string, string> = {};
    ids.forEach((id, i) => (m[id] = SVC_COLORS[i % SVC_COLORS.length]));
    return m;
  });
  const byId = $derived(new Map(spans.map((s) => [s.spanId, s])));
  function depth(s: Span): number {
    let d = 0;
    let cur: Span | undefined = s;
    const seen = new Set<string>();
    while (cur?.parentSpanId && byId.has(cur.parentSpanId) && !seen.has(cur.parentSpanId)) {
      seen.add(cur.parentSpanId);
      cur = byId.get(cur.parentSpanId);
      d++;
    }
    return Math.min(d, 8);
  }
  const ordered = $derived([...spans].sort((a, b) => int64(a.startUnixNano) - int64(b.startUnixNano)));
  const root = $derived(ordered[0]);
  const serviceCount = $derived(new Set(spans.map((s) => s.serviceId)).size);
  const durationMs = $derived((bounds.total / 1_000_000).toFixed(1));
  const hasError = $derived(spans.some((s) => int64(s.statusCode) === 2));
  const sessionId = $derived(spans.map((s) => s.attributes?.["tamp.session.id"]).find((v) => !!v) ?? null);
  const selected = $derived(selectedId ? byId.get(selectedId) : undefined);
  // Env/version tags for the header, resolved from the root span's entity ids (TOBS-28).
  const envName = $derived(root ? entities.envName(projectId, root.environmentId) : null);
  const versionName = $derived(root ? entities.versionName(projectId, root.versionId) : null);

  function left(s: Span): number {
    return ((int64(s.startUnixNano) - bounds.min) / bounds.total) * 100;
  }
  function width(s: Span): number {
    return Math.max(0.5, (int64(s.durationNano) / bounds.total) * 100);
  }
  function ms(v: number | string | undefined): string {
    return (int64(v) / 1_000_000).toFixed(1) + " ms";
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
    services.loadFor(projectId);
    entities.loadFor(projectId);
    load();
  });

  // Resolve the selected error span's fingerprint to its Issue, for the exception->issue link (TOBS-28).
  $effect(() => {
    const fp = selected?.fingerprint;
    selectedIssueId = null;
    if (!fp) return;
    let stale = false;
    (async () => {
      const { data } = await api.GET("/api/projects/{projectId}/issues/by-fingerprint", {
        params: { path: { projectId }, query: { fingerprint: fp } },
      });
      if (!stale && data) selectedIssueId = data.id ?? null;
    })();
    return () => { stale = true; };
  });
</script>

{#if loading}
  <LoadingState rows={6} />
{:else if error}
  <ErrorState message={`Could not load trace (${error}).`} onretry={load} />
{:else if trace}
  <header class="head">
    <div class="titles">
      <div class="pills">
        <span class="pill {hasError ? 'err' : 'ok'}">{hasError ? "Error" : "OK"}</span>
        {#if root}<span class="tag mono">{services.name(projectId, root.serviceId)}</span>{/if}
        {#if envName}<span class="tag env mono" title="Environment">{envName}</span>{/if}
        {#if versionName}<span class="tag ver mono" title="Version">{versionName}</span>{/if}
      </div>
      <h1>{root?.name ?? "Trace"}</h1>
      <p class="mono meta muted">{traceId} · {durationMs} ms · {spans.length} spans · {serviceCount} svc{#if root} · {nanosToTime(root.startUnixNano)}{/if}</p>
    </div>
    {#if sessionId}
      <a class="btn pri" href={router.projectHref(projectId, `/replay/${encodeURIComponent(sessionId)}`)} use:link data-keep-filters="true"><Icon name="play" size={14} />Replay session</a>
    {/if}
  </header>

  <Panel label="Correlation walk"><CorrelationWalk {projectId} current="trace" {traceId} /></Panel>

  <Panel label={`Waterfall · ${spans.length} spans`}>
    {#if spans.length === 0}
      <EmptyState message="No spans for this trace." />
    {:else}
      <div class="axis mono muted"><span>0 ms</span><span>{durationMs} ms</span></div>
      {#each ordered as span (span.spanId)}
        <button class="span-row" class:sel={selectedId === span.spanId} class:err={int64(span.statusCode) === 2} onclick={() => (selectedId = selectedId === span.spanId ? null : (span.spanId ?? null))}>
          <span class="name-cell" style="padding-left:{depth(span) * 14}px">
            <span class="chip" style="background:{serviceColor[span.serviceId ?? '']}"></span>
            <span class="name mono" title={span.name}>{span.name}</span>
          </span>
          <span class="track"><span class="bar" class:err={int64(span.statusCode) === 2} style="left:{left(span)}%;width:{width(span)}%;background:{int64(span.statusCode) === 2 ? 'var(--err)' : serviceColor[span.serviceId ?? '']}"></span></span>
          <span class="dur num mono muted">{ms(span.durationNano)}</span>
        </button>
      {/each}
      <div class="legend">
        {#each [...new Set(spans.map((s) => s.serviceId ?? ''))] as id (id)}
          <span class="leg"><span class="chip" style="background:{serviceColor[id]}"></span>{services.name(projectId, id)}</span>
        {/each}
      </div>
    {/if}
  </Panel>

  {#if selected}
    <Panel label="Selected span">
      <div class="sel-head"><strong class="mono">{selected.name}</strong><span class="tag">{services.name(projectId, selected.serviceId)}</span><span class="pill {int64(selected.statusCode) === 2 ? 'err' : 'ok'}">{int64(selected.statusCode) === 2 ? "Error" : "OK"}</span></div>
      {#if selected.attributes?.["exception.type"] || selected.attributes?.["exception.message"]}
        <div class="exc">
          <div class="exc-head">
            <span class="exc-type mono">{selected.attributes?.["exception.type"] ?? "Exception"}</span>
            {#if selectedIssueId}
              <a class="exc-issue" href={router.projectHref(projectId, `/issues/${selectedIssueId}`)} use:link data-keep-filters="true">View issue</a>
            {/if}
          </div>
          {#if selected.attributes?.["exception.message"]}
            <div class="exc-msg mono">{selected.attributes["exception.message"]}</div>
          {/if}
          {#if selected.attributes?.["exception.stacktrace"]}
            <pre class="exc-trace mono">{selected.attributes["exception.stacktrace"]}</pre>
          {/if}
        </div>
      {/if}
      <dl class="attrs">
        <dt class="muted">span id</dt><dd class="mono">{selected.spanId}</dd>
        <dt class="muted">duration</dt><dd class="mono">{ms(selected.durationNano)}</dd>
        {#if selected.statusMessage}<dt class="muted">status</dt><dd class="mono">{selected.statusMessage}</dd>{/if}
        {#each Object.entries(selected.attributes ?? {}).filter(([k]) => !k.startsWith("exception.")) as [k, v] (k)}
          <dt class="muted mono akey">{k}</dt>
          <dd class="mono">
            {#if k === "tamp.session.id"}<a href={router.projectHref(projectId, `/replay/${encodeURIComponent(v)}`)} use:link data-keep-filters="true">{v}</a>
            {:else}{v}{/if}
          </dd>
        {/each}
      </dl>
    </Panel>
  {/if}

  <Panel label={`Logs on this trace · ${trace.logs.length}`}>
    {#if trace.logs.length === 0}
      <EmptyState message="No logs on this trace." />
    {:else}
      {#each trace.logs as log, i (i)}
        <div class="log" class:errrow={severityClass(log.severityNumber) === 'lvl-err'}>
          <span class="mono muted">{nanosToTime(log.timeUnixNano)}</span>
          <span class="lvl {severityClass(log.severityNumber)}">{severityLabel(log.severityNumber)}</span>
          <span class="mono">{log.body ?? ''}</span>
        </div>
      {/each}
    {/if}
  </Panel>
{/if}

<style>
  .head { display: flex; justify-content: space-between; gap: var(--gap-4); flex-wrap: wrap; align-items: flex-start; }
  .titles { display: flex; flex-direction: column; gap: var(--gap-2); min-width: 0; }
  .titles h1 { margin: 0; }
  .pills { display: flex; gap: var(--gap-2); align-items: center; }
  .meta { word-break: break-all; }
  .pill.ok { background: var(--res-bg); color: var(--res-fg); }
  .pill.err { background: var(--err-bg); color: var(--err); }
  .axis { display: flex; justify-content: space-between; margin-bottom: 6px; font-size: 11px; }
  .span-row { display: grid; grid-template-columns: 240px minmax(0, 1fr) 80px; gap: var(--gap-3); align-items: center; padding: 4px 0; width: 100%; background: none; border: 0; color: var(--text); font: inherit; cursor: pointer; text-align: left; border-radius: var(--r-ctl); }
  .span-row:hover { background: var(--surface-hover); }
  .span-row.sel { background: var(--regr-wash); box-shadow: inset 3px 0 0 var(--regr-fg); }
  .span-row.err .name { color: var(--err); }
  .name-cell { display: flex; align-items: center; gap: 6px; min-width: 0; }
  .chip { width: 8px; height: 8px; border-radius: 2px; flex-shrink: 0; }
  .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .track { position: relative; height: 16px; background: var(--table-bg); border-radius: var(--r-tag); }
  .bar { position: absolute; top: 2px; bottom: 2px; border-radius: var(--r-tag); min-width: 2px; }
  .bar.err { box-shadow: 0 0 0 1px var(--err-outline); }
  .legend { display: flex; flex-wrap: wrap; gap: var(--gap-3); margin-top: var(--gap-3); padding-top: var(--gap-2); border-top: 1px solid var(--divider); }
  .leg { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--text-2); }
  .sel-head { display: flex; align-items: center; gap: var(--gap-2); margin-bottom: var(--gap-3); }
  .exc { border: 1px solid var(--err-outline); background: var(--err-bg); border-radius: var(--r-ctl); padding: var(--gap-2) var(--gap-3); margin-bottom: var(--gap-3); display: flex; flex-direction: column; gap: 4px; }
  .exc-head { display: flex; align-items: center; justify-content: space-between; gap: var(--gap-2); flex-wrap: wrap; }
  .exc-type { color: var(--err); font-weight: 600; word-break: break-all; }
  .exc-issue { color: var(--regr-fg); text-decoration: none; font-size: var(--fs-label); white-space: nowrap; }
  .exc-issue:hover { text-decoration: underline; }
  .tag.env { background: var(--unres-bg); color: var(--unres-fg); }
  .tag.ver { background: var(--regr-bg); color: var(--regr-fg); }
  .exc-msg { color: var(--text); word-break: break-word; }
  .exc-trace { margin: 4px 0 0; max-height: 220px; overflow: auto; font-size: 12px; color: var(--text-2); white-space: pre; background: var(--table-bg); border-radius: var(--r-tag); padding: var(--gap-2); }
  .attrs { display: grid; grid-template-columns: 200px 1fr; gap: 4px var(--gap-4); margin: 0; }
  .attrs dd { margin: 0; word-break: break-all; }
  .akey { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .log { display: grid; grid-template-columns: 96px 60px minmax(0, 1fr); gap: var(--gap-3); padding: 4px 0; border-top: 1px solid var(--divider); }
  .log.errrow { background: var(--err-bg); }
  .lvl { font-size: var(--fs-label); font-weight: 600; }
  .lvl-err { color: var(--err); }
  .lvl-warn { color: var(--warn); }
  .lvl-info { color: var(--muted); }
</style>
