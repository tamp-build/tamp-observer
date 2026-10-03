<script lang="ts">
  // Issues list (README 7.1), wired to GET /api/projects/{id}/issues. Status tabs filter server-side; the
  // command-palette query (?q=) is applied client-side over title/type until a query-syntax endpoint exists.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import { instance } from "../stores/instance.svelte";
  import FilterBar from "../components/shell/FilterBar.svelte";
  import StatusPill from "../components/ui/StatusPill.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import { issueStatusLabel, int64, timeAgo } from "../format";
  import { guard, timeout } from "../net";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  type Issue = components["schemas"]["Issue"];

  const TABS: { label: string; status: string }[] = [
    { label: "Unresolved", status: "Unresolved" },
    { label: "Regressed", status: "Regressed" },
    { label: "Resolved", status: "Resolved" },
    { label: "Muted", status: "Ignored" },
  ];

  let activeTab = $state("Unresolved");
  let issues = $state<Issue[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  const q = $derived(new URLSearchParams(router.route.path && window.location.search).get("q") ?? "");
  const projectName = $derived(instance.project(projectId)?.name ?? "Project");

  const shown = $derived(
    q
      ? issues.filter(
          (i) =>
            i.title.toLowerCase().includes(q.toLowerCase()) ||
            (i.errorType ?? "").toLowerCase().includes(q.toLowerCase()),
        )
      : issues,
  );

  async function load() {
    loading = true;
    error = null;
    const status = TABS.find((t) => t.label === activeTab)?.status;
    error = await guard("load issues", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/issues", {
        params: { path: { projectId }, query: { status, limit: 100 } },
        ...timeout(),
      });
      if (data) issues = data;
      return response;
    });
    loading = false;
  }

  function setTab(label: string) {
    activeTab = label;
    load();
  }

  $effect(() => {
    void projectId;
    load();
  });
</script>

<header class="surface-head">
  <div>
    <p class="h">{projectName}</p>
    <h1>Issues</h1>
  </div>
  <FilterBar />
</header>

<div class="tabs">
  {#each TABS as tab (tab.label)}
    <button class="tab" class:on={activeTab === tab.label} onclick={() => setTab(tab.label)}>{tab.label}</button>
  {/each}
</div>

<section class="panel">
  {#if loading}
    <LoadingState rows={6} />
  {:else if error}
    <ErrorState message={`Could not load issues (${error}).`} onretry={load} />
  {:else if shown.length === 0}
    <EmptyState message={q ? `No ${activeTab.toLowerCase()} issues match "${q}".` : `No ${activeTab.toLowerCase()} issues in this window.`} />
  {:else}
    <div class="scroll-x">
      <div class="tr th">
        <span>Issue</span>
        <span class="num">Events</span>
        <span>Last seen</span>
        <span>Versions</span>
      </div>
      {#each shown as issue (issue.id)}
        <a class="tr row" href={router.projectHref(projectId, `/issues/${issue.id}`)} use:link data-keep-filters="true">
          <span class="issue">
            <StatusPill status={issueStatusLabel(issue.status)} />
            <span class="title">
              <strong>{issue.errorType ?? issue.title}</strong>
              <span class="msg muted">{issue.title}</span>
            </span>
          </span>
          <span class="num mono">{int64(issue.count).toLocaleString()}</span>
          <span class="muted">{timeAgo(issue.lastSeenAtUtc)}</span>
          <span class="mono muted">{int64(issue.firstSeenVersionSequence)} → {int64(issue.lastSeenVersionSequence)}</span>
        </a>
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
  .tabs {
    display: flex;
    gap: var(--gap-1);
    border-bottom: 1px solid var(--border);
  }
  .tr {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 90px 110px 140px;
    gap: var(--gap-4);
    align-items: center;
    padding: 10px var(--gap-4);
    min-width: 640px;
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
  .issue {
    display: flex;
    align-items: center;
    gap: var(--gap-3);
    min-width: 0;
  }
  .title {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .msg {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
