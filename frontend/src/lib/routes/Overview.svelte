<script lang="ts">
  // Project overview (README 7.9), matched to the design: change banner, 4 KPI tiles, needs-attention issues,
  // operations table, and a right rail (sessions + alerts). Merges the built reads (latency, operations, issues,
  // alerts, sessions). Time-series (per-tile sparklines, the error-rate chart) and the releases rail need a
  // metrics history / release feed we do not collect yet, so those render honest "not collected yet".
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import { instance } from "../stores/instance.svelte";
  import { services } from "../stores/services.svelte";
  import { filters } from "../stores/filters.svelte";
  import FilterBar from "../components/shell/FilterBar.svelte";
  import StatusPill from "../components/ui/StatusPill.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import Icon from "../components/ui/Icon.svelte";
  import { issueStatusLabel, int64, timeAgo } from "../format";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  type Latency = components["schemas"]["LatencyPercentiles"];
  type Op = components["schemas"]["OperationStat"];
  type Issue = components["schemas"]["Issue"];
  type Counts = components["schemas"]["IssueCounts"];
  type Alerts = components["schemas"]["AlertsView"];
  type Session = components["schemas"]["ReplaySessionSummary"];

  let latency = $state<Latency | null>(null);
  let ops = $state<Op[]>([]);
  let issues = $state<Issue[]>([]);
  let counts = $state<Counts | null>(null);
  let alerts = $state<Alerts | null>(null);
  let sessions = $state<Session[]>([]);
  let loading = $state(true);

  const projectName = $derived(instance.project(projectId)?.name ?? "Project");
  const totalCalls = $derived(ops.reduce((a, o) => a + int64(o.count), 0));
  const totalErrors = $derived(ops.reduce((a, o) => a + int64(o.errorCount), 0));
  const errorRate = $derived(totalCalls > 0 ? (totalErrors / totalCalls) * 100 : 0);
  const openIssues = $derived(counts ? int64(counts.unresolved) + int64(counts.regressed) : 0);

  function ms(v: number | string | undefined): string {
    return (int64(v) / 1_000_000).toFixed(0) + " ms";
  }
  function opErrorRate(o: Op): number {
    const c = int64(o.count);
    return c > 0 ? Math.round((int64(o.errorCount) / c) * 100) : 0;
  }

  async function load() {
    loading = true;
    const { start, end } = filters.rangeNanos;
    const [lat, op, iss, c, al, se] = await Promise.all([
      api.GET("/api/projects/{projectId}/latency", { params: { path: { projectId }, query: { start, end } } }),
      api.GET("/api/projects/{projectId}/operations", { params: { path: { projectId }, query: { start, end, limit: 10 } } }),
      api.GET("/api/projects/{projectId}/issues", { params: { path: { projectId }, query: { limit: 5 } } }),
      api.GET("/api/projects/{projectId}/issues/counts", { params: { path: { projectId } } }),
      api.GET("/api/projects/{projectId}/alerts", { params: { path: { projectId } } }),
      api.GET("/api/projects/{projectId}/sessions", { params: { path: { projectId } } }),
    ]);
    latency = lat.data ?? null;
    ops = op.data ?? [];
    issues = iss.data ?? [];
    counts = c.data ?? null;
    alerts = al.data ?? null;
    sessions = (se.data ?? []).slice(0, 4);
    loading = false;
  }

  $effect(() => {
    void projectId;
    void filters.window;
    services.loadFor(projectId);
    load();
  });
</script>

<header class="surface-head">
  <div><p class="h">{projectName}</p><h1>Overview</h1></div>
  <FilterBar />
</header>

{#if loading}
  <LoadingState rows={6} />
{:else}
  {#if counts && int64(counts.regressed) > 0}
    <section class="callout">
      <span class="pill regr"><Icon name="refresh" size={12} />Regressions</span>
      <p class="msg">{counts.regressed} issue{counts.regressed === 1 ? "" : "s"} came back in a later version. Review what changed.</p>
      <a class="btn" href={router.projectHref(projectId, "/issues")} use:link data-keep-filters="true">See issues</a>
    </section>
  {/if}

  <!-- KPI tiles -->
  <div class="tiles">
    <div class="tile"><span class="h">Requests</span><span class="big">{int64(latency?.count).toLocaleString()}</span><span class="muted small">spans in window</span></div>
    <div class="tile"><span class="h">Error rate</span><span class="big" class:up={errorRate >= 5}>{errorRate.toFixed(1)}%</span><span class="muted small">{totalErrors.toLocaleString()} failed of {totalCalls.toLocaleString()}</span></div>
    <div class="tile"><span class="h">Latency p95</span><span class="big">{ms(latency?.p95)}</span><span class="muted small">p50 {ms(latency?.p50)} · p99 {ms(latency?.p99)}</span></div>
    <a class="tile linked" href={router.projectHref(projectId, "/issues")} use:link data-keep-filters="true">
      <span class="h">Open issues</span><span class="big">{openIssues}</span>
      <span class="pills">{#if counts && int64(counts.regressed) > 0}<span class="pill regr">{counts.regressed} regressed</span>{/if}<span class="pill unres">{counts?.unresolved ?? 0} unresolved</span></span>
    </a>
  </div>

  <div class="cols">
    <div class="main-col">
      <!-- Needs attention -->
      <section class="panel">
        <div class="panel-head"><h2 class="h">Needs attention</h2><a href={router.projectHref(projectId, "/issues")} use:link data-keep-filters="true">All {counts?.total ?? ""} issues</a></div>
        {#if issues.length === 0}
          <EmptyState message="No issues yet." />
        {:else}
          <div class="att th"><span>Issue</span><span>24h</span><span class="num">Events</span><span class="num">Sessions</span></div>
          {#each issues as issue (issue.id)}
            {@const lbl = issueStatusLabel(issue.status)}
            <a class="att row" href={router.projectHref(projectId, `/issues/${issue.id}`)} use:link data-keep-filters="true">
              <span class="att-issue">
                <span class="att-line"><StatusPill status={lbl} /><strong>{issue.errorType ?? "Error"}</strong><span class="muted msg">{issue.title}</span></span>
                <span class="mono muted att-meta">{services.name(projectId, issue.serviceId)} · since v{int64(issue.firstSeenVersionSequence)}</span>
              </span>
              <svg class="spark" width="90" height="24" viewBox="0 0 90 24"><line x1="0" y1="20" x2="90" y2="20" stroke="var(--divider)" stroke-width="1.5" /></svg>
              <span class="num mono">{int64(issue.count).toLocaleString()}</span>
              <span class="num mono muted">—</span>
            </a>
          {/each}
        {/if}
      </section>

      <!-- Error rate chart (gap) -->
      <section class="panel pad">
        <div class="panel-head" style="padding:0 0 8px"><h2 class="h">Error rate · last 24h</h2></div>
        <p class="muted small">A per-bucket error-rate time series with deploy markers needs a metrics history store (not collected yet).</p>
      </section>

      <!-- Operations -->
      <section class="panel">
        <div class="panel-head"><h2 class="h">Operations</h2><span class="muted small">sorted by calls · click to filter (soon)</span></div>
        {#if ops.length === 0}
          <EmptyState message="No operations in this window." />
        {:else}
          <div class="op th"><span>Operation</span><span class="num">Calls</span><span class="num">Errors</span><span>Error rate</span><span class="num">P95</span></div>
          {#each ops as op (op.operation)}
            <div class="op">
              <span class="mono opname">{op.operation}</span>
              <span class="num mono">{int64(op.count).toLocaleString()}</span>
              <span class="num mono" class:err={int64(op.errorCount) > 0}>{int64(op.errorCount).toLocaleString()}</span>
              <span class="rate"><span class="bar"><span style="width:{opErrorRate(op)}%;background:var(--err)"></span></span><span class="mono pctn">{opErrorRate(op)}%</span></span>
              <span class="num mono muted">—</span>
            </div>
          {/each}
        {/if}
      </section>
    </div>

    <div class="side-col">
      <!-- Sessions worth replaying -->
      <section class="panel">
        <div class="panel-head"><h2 class="h">Recent sessions</h2><a href={router.projectHref(projectId, "/replay")} use:link data-keep-filters="true">All</a></div>
        {#if sessions.length === 0}
          <div class="pad"><p class="muted small">No replay sessions captured yet.</p></div>
        {:else}
          {#each sessions as s (s.sessionId)}
            <a class="srow" href={router.projectHref(projectId, `/replay/${encodeURIComponent(s.sessionId)}`)} use:link data-keep-filters="true">
              <span class="mono sid">{s.sessionId.slice(0, 12)}</span>
              <span class="muted small">{int64(s.eventCount)} events · {timeAgo(s.startedAtUtc)}</span>
            </a>
          {/each}
        {/if}
      </section>

      <!-- Alerts -->
      <section class="panel">
        <div class="panel-head"><h2 class="h">Alerts</h2><a href={router.projectHref(projectId, "/alerts")} use:link data-keep-filters="true">All</a></div>
        {#if !alerts || alerts.history.length === 0}
          <div class="pad"><p class="muted small">No alerts fired in this window.</p></div>
        {:else}
          {#each alerts.history.slice(0, 5) as ev (ev.issueId + ev.atUtc)}
            <a class="srow" href={router.projectHref(projectId, `/issues/${ev.issueId}`)} use:link data-keep-filters="true">
              <span class="atitle">{ev.title}</span>
              <span class="muted small">{ev.kind} · {timeAgo(ev.atUtc)}</span>
            </a>
          {/each}
        {/if}
      </section>

      <section class="panel pad">
        <h2 class="h">Releases</h2>
        <p class="muted small">Per-release error-rate deltas need a release feed with deploy times (not collected yet).</p>
      </section>
    </div>
  </div>
{/if}

<style>
  .surface-head { display: flex; align-items: flex-end; justify-content: space-between; gap: var(--gap-4); flex-wrap: wrap; }
  .surface-head h1 { margin: 0; }
  .callout { display: flex; flex-wrap: wrap; align-items: center; gap: var(--gap-3) var(--gap-4); padding: 12px 16px; border: 1px solid var(--regr-border); border-radius: var(--r-panel); background: var(--regr-wash); }
  .callout .msg { margin: 0; flex: 1 1 320px; color: #f1dcc8; }
  .small { font-size: 12px; }

  .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(220px, 100%), 1fr)); gap: var(--gap-3); }
  .tile { display: flex; flex-direction: column; gap: 6px; padding: 14px 16px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--r-panel); }
  .tile.linked:hover { border-color: var(--accent); }
  .big { font-size: 26px; font-weight: 650; letter-spacing: -0.01em; font-variant-numeric: tabular-nums; line-height: 1.1; }
  .up { color: var(--err); }
  .pills { display: flex; gap: var(--gap-1); flex-wrap: wrap; }

  .cols { display: flex; flex-wrap: wrap; gap: var(--gap-3); align-items: flex-start; }
  .main-col { flex: 3 1 560px; min-width: 0; display: flex; flex-direction: column; gap: var(--gap-3); }
  .side-col { flex: 1 1 280px; min-width: 0; display: flex; flex-direction: column; gap: var(--gap-3); }
  .panel-head { display: flex; align-items: center; justify-content: space-between; gap: var(--gap-3); padding: 12px 16px; border-bottom: 1px solid var(--divider); }
  .pad { padding: var(--gap-4); }

  .att { display: grid; grid-template-columns: minmax(0, 1fr) 100px 64px 72px; gap: 14px; align-items: center; padding: 11px 16px; border-top: 1px solid var(--divider); color: var(--text); }
  .att.th { border-top: 0; font-size: var(--fs-label); text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); font-weight: 600; padding: 8px 16px; }
  .att.row:hover { background: var(--surface-hover); }
  .att-issue { min-width: 0; display: flex; flex-direction: column; gap: 3px; }
  .att-line { display: flex; gap: 8px; align-items: center; min-width: 0; }
  .msg { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
  .att-meta { font-size: 12px; }
  .spark { display: block; }

  .op { display: grid; grid-template-columns: minmax(140px, 1.3fr) 70px 70px minmax(120px, 1fr) 80px; gap: 14px; align-items: center; padding: 9px 16px; border-top: 1px solid var(--divider); }
  .op.th { border-top: 0; font-size: var(--fs-label); text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); font-weight: 600; padding: 8px 16px; }
  .opname { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .err { color: var(--err); }
  .rate { display: flex; align-items: center; gap: 8px; }
  .bar { flex: 1; height: 6px; border-radius: 3px; background: var(--table-bg); overflow: hidden; }
  .bar span { display: block; height: 6px; border-radius: 3px; }
  .pctn { width: 36px; text-align: right; }

  .srow { display: flex; flex-direction: column; gap: 2px; padding: 10px 16px; border-top: 1px solid var(--divider); color: var(--text); }
  .srow:first-of-type { border-top: 0; }
  .srow:hover { background: var(--surface-hover); }
  .sid { color: var(--text); }
  .atitle { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
