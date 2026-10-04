<script lang="ts">
  // Logs explorer (TOBS-38): server-side filtering (service / category / free-text search / errors-only),
  // keyset "load older" paging, and a pull-based live tail. Wired to GET /api/projects/{id}/logs and
  // .../logs/tail over the global time window. Correlation keys (trace / session) link out to the trace and
  // replay surfaces.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { filters } from "../stores/filters.svelte";
  import FilterBar from "../components/shell/FilterBar.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import EmptyState from "../components/ui/EmptyState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import { nanosToTime, severityLabel, severityClass } from "../format";
  import { guard, timeout } from "../net";
  import { services } from "../stores/services.svelte";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  type Log = components["schemas"]["IngestedLog"];

  const PAGE = 200;
  const TAIL_MS = 2000;

  let logs = $state<Log[]>([]);
  let loading = $state(true);
  let loadingOlder = $state(false);
  let error = $state<string | null>(null);
  let errorsOnly = $state(false);
  let serviceFilter = $state<string | null>(null);
  // Server-side category filter (log.category, e.g. db.query / app.auth).
  let categoryFilter = $state<string | null>(null);
  // Free-text body search; debounced into `search` so each keystroke doesn't fire a request.
  let searchInput = $state("");
  let search = $state("");
  let searchTimer: ReturnType<typeof setTimeout> | undefined;
  // Live tail: when on, poll for rows newer than the cursor and prepend, instead of reloading the window.
  let live = $state(false);
  // True while the last page came back full, so there may be older rows to fetch.
  let hasMore = $state(false);

  const categoryOf = (log: Log): string | null => log.attributes?.["log.category"] ?? null;
  const sessionOf = (log: Log): string | null => log.attributes?.["tamp.session.id"] ?? null;

  // Distinct categories seen in the loaded rows, for the dropdown (the filter itself is applied server-side).
  let categories = $derived(
    [...new Set(logs.map(categoryOf).filter((c): c is string => !!c))].sort(),
  );
  let serviceList = $derived(services.list(projectId));

  // The query params shared by the window load, load-older, and tail.
  function commonQuery() {
    return {
      service: serviceFilter ?? undefined,
      minSeverity: errorsOnly ? 17 : undefined,
      category: categoryFilter ?? undefined,
      q: search.trim() ? search.trim() : undefined,
    };
  }

  async function load() {
    loading = true;
    error = null;
    logs = [];
    const { start, end } = filters.rangeNanos;
    error = await guard("load logs", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/logs", {
        params: { path: { projectId }, query: { start, end, limit: PAGE, ...commonQuery() } },
        ...timeout(),
      });
      if (data) logs = data;
      hasMore = (data?.length ?? 0) >= PAGE;
      return response;
    });
    loading = false;
  }

  async function loadOlder() {
    if (loadingOlder || logs.length === 0) return;
    loadingOlder = true;
    const { start, end } = filters.rangeNanos;
    const before = Number(logs[logs.length - 1].timeUnixNano);
    await guard("load older logs", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/logs", {
        params: { path: { projectId }, query: { start, end, limit: PAGE, before, ...commonQuery() } },
        ...timeout(),
      });
      if (data && data.length > 0) logs = [...logs, ...data];
      hasMore = (data?.length ?? 0) >= PAGE;
      return response;
    });
    loadingOlder = false;
  }

  async function pollTail() {
    // Cursor = newest row we hold (rows are newest-first), else the window start.
    const after = Number(logs.length > 0 ? logs[0].timeUnixNano : filters.rangeNanos.start);
    await guard("tail logs", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/logs/tail", {
        params: { path: { projectId }, query: { after, limit: PAGE, ...commonQuery() } },
        ...timeout(),
      });
      if (data && data.length > 0) {
        // Tail returns oldest-first; reverse so the newest ends on top of the newest-first list.
        logs = [...data.reverse(), ...logs];
      }
      return response;
    });
  }

  function onSearchInput() {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => (search = searchInput), 350);
  }

  // Window load: re-runs whenever the project, window, or any server-side filter changes -- but not while
  // live, where the tail poll owns the list.
  $effect(() => {
    void projectId;
    void filters.window;
    void errorsOnly;
    void serviceFilter;
    void categoryFilter;
    void search;
    services.loadFor(projectId);
    if (!live) load();
  });

  // Live tail lifecycle: seed the window once, then poll on an interval; stop (and reload) when toggled off.
  $effect(() => {
    if (!live) return;
    const id = setInterval(pollTail, TAIL_MS);
    return () => clearInterval(id);
  });

  function toggleLive() {
    live = !live;
    if (!live) load();
  }
</script>

<header class="surface-head">
  <h1>Logs</h1>
  <FilterBar />
</header>

<div class="toolbar">
  <input
    class="search"
    type="search"
    placeholder="Search message…"
    bind:value={searchInput}
    oninput={onSearchInput}
  />
  {#if serviceList.length > 0}
    <label class="check">
      Service
      <select bind:value={serviceFilter}>
        <option value={null}>All</option>
        {#each serviceList as s (s.id)}
          <option value={s.id}>{s.name}</option>
        {/each}
      </select>
    </label>
  {/if}
  {#if categories.length > 0}
    <label class="check">
      Category
      <select bind:value={categoryFilter}>
        <option value={null}>All</option>
        {#each categories as c (c)}
          <option value={c}>{c}</option>
        {/each}
      </select>
    </label>
  {/if}
  <label class="check">
    <input type="checkbox" bind:checked={errorsOnly} /> Errors only
  </label>
  <button class="live" class:on={live} onclick={toggleLive} title="Poll for new logs">
    <span class="dot" class:pulse={live}></span>{live ? "Live" : "Go live"}
  </button>
</div>

<section class="panel">
  {#if loading}
    <LoadingState rows={8} />
  {:else if error}
    <ErrorState message={`Could not load logs (${error}).`} onretry={load} />
  {:else if logs.length === 0}
    <EmptyState message="No logs match these filters in this window." />
  {:else}
    <div class="scroll-x">
      {#each logs as log, i (i)}
        <div class="log-row" class:err={severityClass(log.severityNumber) === 'lvl-err'}>
          <span class="mono muted time">{nanosToTime(log.timeUnixNano)}</span>
          <span class="lvl {severityClass(log.severityNumber)}">{severityLabel(log.severityNumber)}</span>
          <span class="svc muted" title={services.name(projectId, log.serviceId)}>{services.name(projectId, log.serviceId)}</span>
          {#if categoryOf(log)}
            <button class="cat" title={`Filter by ${categoryOf(log)}`} onclick={() => (categoryFilter = categoryOf(log))}>{categoryOf(log)}</button>
          {:else}
            <span class="cat muted">·</span>
          {/if}
          <span class="mono body">{log.body ?? ''}</span>
          <span class="corr">
            {#if log.traceId}
              <a class="link" href={`/p/${projectId}/traces/${log.traceId}`} data-keep-filters="true" title="Open trace">trace</a>
            {/if}
            {#if sessionOf(log)}
              <a class="link" href={`/p/${projectId}/replay/${sessionOf(log)}`} data-keep-filters="true" title="Open session replay">session</a>
            {/if}
          </span>
        </div>
      {/each}
    </div>
    {#if !live && hasMore}
      <div class="more">
        <button onclick={loadOlder} disabled={loadingOlder}>
          {loadingOlder ? "Loading…" : "Load older"}
        </button>
      </div>
    {/if}
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
    align-items: center;
    flex-wrap: wrap;
  }
  .search {
    flex: 1 1 220px;
    min-width: 180px;
    padding: 4px 8px;
    background: var(--surface-2, var(--bg-2));
    color: var(--text-1);
    border: 1px solid var(--divider);
    border-radius: var(--radius-1, 4px);
    font-size: var(--fs-label);
  }
  .check {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-2);
    color: var(--text-2);
  }
  .live {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-2);
    padding: 4px 10px;
    background: none;
    color: var(--text-2);
    border: 1px solid var(--divider);
    border-radius: var(--radius-1, 4px);
    cursor: pointer;
  }
  .live.on {
    color: var(--text-1);
    border-color: var(--ok, var(--accent));
  }
  .live .dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--muted);
  }
  .live .dot.pulse {
    background: var(--ok, var(--accent));
    animation: pulse 1.4s ease-in-out infinite;
  }
  @keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.3; }
  }
  .log-row {
    display: grid;
    grid-template-columns: 96px 60px 140px 120px minmax(0, 1fr) 96px;
    gap: var(--gap-3);
    align-items: baseline;
    padding: 5px var(--gap-4);
    border-top: 1px solid var(--divider);
    min-width: 900px;
  }
  .svc {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: var(--fs-label);
  }
  .cat {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: var(--fs-label);
    font-family: var(--font-mono, monospace);
    color: var(--text-2);
    text-align: left;
    background: none;
    border: none;
    padding: 0;
    cursor: pointer;
  }
  button.cat:hover {
    color: var(--text-1);
    text-decoration: underline;
  }
  .log-row.err {
    background: var(--err-bg);
  }
  .body {
    white-space: pre-wrap;
    word-break: break-word;
  }
  .corr {
    display: inline-flex;
    gap: var(--gap-2);
    font-size: var(--fs-label);
    justify-content: flex-end;
  }
  .link {
    color: var(--accent, var(--text-2));
    text-decoration: none;
  }
  .link:hover {
    text-decoration: underline;
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
  .more {
    display: flex;
    justify-content: center;
    padding: var(--gap-3);
  }
  .more button {
    padding: 4px 16px;
    background: none;
    color: var(--text-2);
    border: 1px solid var(--divider);
    border-radius: var(--radius-1, 4px);
    cursor: pointer;
  }
  .more button:hover:not(:disabled) {
    color: var(--text-1);
  }
  .more button:disabled {
    opacity: 0.5;
    cursor: default;
  }
</style>
