<script lang="ts">
  // Issues list (README 7.1), matched to the design: regression callout, status tabs with counts, query +
  // Service/Area/Sort controls, bulk actions, and the dense issue table. Wired to GET /issues (+ status filter),
  // /issues/counts and /services. Per-issue sparkline and session counts need an occurrence histogram we do not
  // collect yet, so those cells show a flat placeholder / dash rather than fabricated data.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import { instance } from "../stores/instance.svelte";
  import { services } from "../stores/services.svelte";
  import { session } from "../stores/session.svelte";
  import FilterBar from "../components/shell/FilterBar.svelte";
  import StatusPill from "../components/ui/StatusPill.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import Icon from "../components/ui/Icon.svelte";
  import { issueStatusLabel, int64, timeAgo } from "../format";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  type Issue = components["schemas"]["Issue"];
  type Counts = components["schemas"]["IssueCounts"];

  const TABS: { label: string; status: string }[] = [
    { label: "Unresolved", status: "Unresolved" },
    { label: "Regressed", status: "Regressed" },
    { label: "Resolved", status: "Resolved" },
    { label: "Muted", status: "Ignored" },
  ];

  let activeTab = $state("Unresolved");
  let issues = $state<Issue[]>([]);
  let counts = $state<Counts | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  const canEdit = $derived(session.can("EditCapturePolicy", { project: projectId }));
  const projectName = $derived(instance.project(projectId)?.name ?? "Project");

  function countFor(label: string): number | null {
    if (!counts) return null;
    switch (label) {
      case "Unresolved": return int64(counts.unresolved);
      case "Regressed": return int64(counts.regressed);
      case "Resolved": return int64(counts.resolved);
      case "Muted": return int64(counts.muted);
      default: return null;
    }
  }
  const sparkColor: Record<string, string> = {
    Unresolved: "var(--unres-fg)", Regressed: "var(--regr-fg)", Resolved: "var(--res-fg)", Muted: "var(--muted)",
  };

  async function load() {
    loading = true;
    error = null;
    const status = TABS.find((t) => t.label === activeTab)?.status;
    const [list, c] = await Promise.all([
      api.GET("/api/projects/{projectId}/issues", { params: { path: { projectId }, query: { status, limit: 100 } } }),
      api.GET("/api/projects/{projectId}/issues/counts", { params: { path: { projectId } } }),
    ]);
    if (list.data) issues = list.data;
    else error = `${list.response.status} ${list.response.statusText}`;
    if (c.data) counts = c.data;
    loading = false;
  }

  function setTab(label: string) {
    activeTab = label;
    load();
  }

  $effect(() => {
    void projectId;
    services.loadFor(projectId);
    load();
  });
</script>

<header class="surface-head">
  <div><p class="h">{projectName}</p><h1>Issues</h1></div>
  <FilterBar />
</header>

{#if counts && int64(counts.regressed) > 0}
  <section class="callout">
    <span class="pill regr"><Icon name="refresh" size={12} />{counts.regressed} regressed</span>
    <p class="msg">{counts.regressed} issue{counts.regressed === 1 ? "" : "s"} you resolved in an earlier version have come back in a later one.</p>
    <button class="btn" onclick={() => setTab("Regressed")}>Review regressions</button>
  </section>
{/if}

<div class="tabbar">
  <div class="tabs">
    {#each TABS as tab (tab.label)}
      <button class="tab" class:on={activeTab === tab.label} onclick={() => setTab(tab.label)}>
        {tab.label}
        {#if countFor(tab.label) !== null}<span class="ct" class:regr-ct={tab.label === "Regressed"}>{countFor(tab.label)}</span>{/if}
      </button>
    {/each}
  </div>
  <div class="tools">
    <div class="field q"><Icon name="search" size={14} /><input class="mono" placeholder="is:{activeTab.toLowerCase()} service:any" aria-label="Filter issues" /></div>
    <button class="btn" disabled>Service</button>
    <button class="btn" disabled>Area</button>
    <button class="btn" disabled>Sort: last seen</button>
  </div>
</div>

{#if canEdit}
  <div class="bulk">
    <span class="muted">0 selected</span>
    <button class="btn" disabled>Resolve</button>
    <button class="btn" disabled>Assign</button>
    <button class="btn" disabled>Tag area</button>
    <button class="btn" disabled>Mute</button>
  </div>
{/if}

<section class="panel table">
  {#if loading}
    <LoadingState rows={6} />
  {:else if error}
    <ErrorState message={`Could not load issues (${error}).`} onretry={load} />
  {:else if issues.length === 0}
    <EmptyState message={`No ${activeTab.toLowerCase()} issues in this window.`} />
  {:else}
    <div class="scroll-x"><div class="grid-wrap">
      <div class="tr th">
        <span></span><span>Issue</span><span>Last 24h</span><span class="num">Events</span>
        <span class="num">Sessions</span><span>Last / first seen</span><span>Versions</span>
      </div>
      {#each issues as issue (issue.id)}
        {@const label = issueStatusLabel(issue.status)}
        <a class="tr row" href={router.projectHref(projectId, `/issues/${issue.id}`)} use:link data-keep-filters="true">
          <input type="checkbox" aria-label="Select issue" onclick={(e) => e.preventDefault()} disabled={!canEdit} />
          <div class="issue">
            <div class="issue-line">
              <StatusPill status={label} />
              <strong class="type">{issue.errorType ?? "Error"}</strong>
              <span class="muted msg">{issue.title}</span>
            </div>
            <div class="issue-meta">
              <span class="mono muted">#{issue.id?.slice(0, 8)}</span>
              <span class="mono svc">{services.name(projectId, issue.serviceId)}</span>
              {#if label === "Regressed"}<span class="n-regr">regressed in v{int64(issue.lastSeenVersionSequence)}</span>{/if}
              {#if label === "Resolved" && issue.resolvedInVersionSequence != null}<span class="n-res">resolved in v{int64(issue.resolvedInVersionSequence)}</span>{/if}
            </div>
          </div>
          <svg class="spark" width="120" height="28" viewBox="0 0 120 28" aria-hidden="true"><line x1="0" y1="24" x2="120" y2="24" stroke={sparkColor[label]} stroke-width="1.5" opacity="0.35" /></svg>
          <span class="num mono">{int64(issue.count).toLocaleString()}</span>
          <span class="num mono muted">—</span>
          <span class="seen"><span>{timeAgo(issue.lastSeenAtUtc)}</span><span class="muted tiny">{timeAgo(issue.firstSeenAtUtc)}</span></span>
          <span class="mono vers">{int64(issue.firstSeenVersionSequence)} → {int64(issue.lastSeenVersionSequence)}</span>
        </a>
      {/each}
    </div></div>
    <p class="muted foot">Showing {issues.length}{counts ? ` of ${counts.total}` : ""} · grouped by fingerprint. Per-issue sparkline and session counts need an occurrence histogram (not collected yet).</p>
  {/if}
</section>

<style>
  .surface-head { display: flex; align-items: flex-end; justify-content: space-between; gap: var(--gap-4); flex-wrap: wrap; }
  .surface-head h1 { margin: 0; }
  .callout { display: flex; flex-wrap: wrap; align-items: center; gap: var(--gap-3) var(--gap-4); padding: 12px 16px; border: 1px solid var(--regr-border); border-radius: var(--r-panel); background: var(--regr-wash); }
  .callout .msg { margin: 0; flex: 1 1 320px; color: #f1dcc8; }
  .tabbar { display: flex; flex-wrap: wrap; align-items: flex-end; justify-content: space-between; gap: 8px 16px; border-bottom: 1px solid var(--border); }
  .tabs { display: flex; flex-wrap: wrap; gap: 2px; }
  .regr-ct { background: var(--regr-bg); color: var(--regr-fg); }
  .tools { display: flex; flex-wrap: wrap; gap: var(--gap-2); padding-bottom: 8px; }
  .q { width: 280px; max-width: 100%; }
  .bulk { display: flex; flex-wrap: wrap; align-items: center; gap: var(--gap-2); }
  .table { background: var(--table-bg); }
  .grid-wrap { min-width: 960px; }
  .tr { display: grid; grid-template-columns: 20px minmax(0, 1fr) 124px 72px 72px 108px 116px; gap: 14px; align-items: center; padding: 10px 14px; border-top: 1px solid var(--divider); }
  .tr.row { color: var(--text); }
  .tr.row:hover { background: var(--surface-hover); }
  .th { border-top: 0; padding: 8px 14px; font-size: var(--fs-label); color: var(--muted); text-transform: uppercase; letter-spacing: 0.06em; font-weight: 600; }
  .issue { min-width: 0; display: flex; flex-direction: column; gap: 5px; }
  .issue-line { display: flex; gap: 8px; align-items: center; min-width: 0; }
  .type { white-space: nowrap; }
  .msg { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
  .issue-meta { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; font-size: 12px; }
  .svc { color: var(--text-2); }
  .n-regr { color: var(--regr-fg); }
  .n-res { color: var(--res-fg); }
  .spark { display: block; }
  .seen { display: flex; flex-direction: column; }
  .tiny { font-size: 11px; }
  .vers { color: var(--text-2); }
  .foot { margin: 0; padding: 10px 14px; border-top: 1px solid var(--divider); font-size: 12px; }
</style>
