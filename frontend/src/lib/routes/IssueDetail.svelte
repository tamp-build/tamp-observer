<script lang="ts">
  // Issue detail (README 7.2 / TOBS-26): header with status + tags + lifecycle + actions, an occurrences chart
  // (from the TOBS-25 issue series), the correlation walk, and a side column (version history + details). The
  // stack trace + breadcrumbs are TOBS-27 (need structured frames) and render a placeholder.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import { session } from "../stores/session.svelte";
  import { services } from "../stores/services.svelte";
  import { filters } from "../stores/filters.svelte";
  import StatusPill from "../components/ui/StatusPill.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import Panel from "../components/ui/Panel.svelte";
  import Icon from "../components/ui/Icon.svelte";
  import CorrelationWalk from "../components/CorrelationWalk.svelte";
  import { issueStatusLabel, int64, timeAgo } from "../format";

  interface Props {
    projectId: string;
    issueId: string;
  }
  let { projectId, issueId }: Props = $props();

  type Issue = components["schemas"]["Issue"];
  type Series = components["schemas"]["IssueSeries"];

  type Stack = components["schemas"]["StackTraceView"];

  let issue = $state<Issue | null>(null);
  let series = $state<Series | null>(null);
  let stack = $state<Stack | null>(null);
  let showFramework = $state(false);
  let showRaw = $state(false);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let copied = $state(false);

  const inAppFrames = $derived((stack?.frames ?? []).filter((f) => f.inApp));
  const frameworkFrames = $derived((stack?.frames ?? []).filter((f) => !f.inApp));

  const canEdit = $derived(session.can("EditCapturePolicy", { project: projectId }));
  const isResolved = $derived(issue?.status === 1);
  const label = $derived(issue ? issueStatusLabel(issue.status) : "Unresolved");
  const buckets = $derived(series?.buckets?.map((n) => int64(n)) ?? []);
  const maxBucket = $derived(Math.max(1, ...buckets));

  async function load() {
    loading = true;
    error = null;
    const { start, end } = filters.rangeNanos;
    const [det, ser, stk] = await Promise.all([
      api.GET("/api/projects/{projectId}/issues/{issueId}", { params: { path: { projectId, issueId } } }),
      api.GET("/api/projects/{projectId}/issues/series", { params: { path: { projectId }, query: { start, end, buckets: 24 } } }),
      api.GET("/api/projects/{projectId}/issues/{issueId}/stacktrace", { params: { path: { projectId, issueId } } }),
    ]);
    if (det.data) issue = det.data;
    else error = `${det.response.status} ${det.response.statusText}`;
    series = (ser.data ?? []).find((s) => s.fingerprint === det.data?.fingerprint) ?? null;
    stack = stk.data ?? null;
    loading = false;
  }

  async function setStatus(status: string) {
    if (!canEdit) return;
    busy = true;
    const { response } = await api.POST("/api/projects/{projectId}/issues/{issueId}/status", {
      params: { path: { projectId, issueId } },
      body: { status, resolvedInVersionSequence: null },
    });
    busy = false;
    if (response.ok) await load();
  }

  async function copyLink() {
    try {
      await navigator.clipboard.writeText(location.href);
      copied = true;
      setTimeout(() => (copied = false), 1500);
    } catch {
      copied = false;
    }
  }

  $effect(() => {
    void issueId;
    services.loadFor(projectId);
    load();
  });
</script>

<nav class="crumbs mono muted">
  <a href={router.projectHref(projectId, "/issues")} use:link data-keep-filters="true">Issues</a>
  <Icon name="chevron-right" size={12} /><span>{issueId.slice(0, 8)}</span>
</nav>

{#if loading}
  <LoadingState rows={4} />
{:else if error}
  <ErrorState message={`Could not load issue (${error}).`} onretry={load} />
{:else if issue}
  <header class="head">
    <div class="titles">
      <div class="pills">
        <StatusPill status={label} version={label === "Regressed" ? `v${int64(issue.lastSeenVersionSequence)}` : null} />
        <span class="tag mono">{services.name(projectId, issue.serviceId)}</span>
      </div>
      <h1>{issue.errorType ?? "Issue"}</h1>
      <p class="mono msg">{issue.title}</p>
      <p class="muted">First seen {timeAgo(issue.firstSeenAtUtc)} · last seen {timeAgo(issue.lastSeenAtUtc)}{#if isResolved && issue.resolvedInVersionSequence != null} · resolved in v{int64(issue.resolvedInVersionSequence)}{/if}</p>
    </div>
    <div class="actions">
      {#if isResolved}
        <button class="btn" disabled={!canEdit || busy} onclick={() => setStatus("Unresolved")}>Reopen</button>
      {:else}
        <button class="btn pri" disabled={!canEdit || busy} onclick={() => setStatus("Resolved")}><Icon name="check" size={14} />Resolve</button>
      {/if}
      <button class="btn" disabled>Assign</button>
      <button class="btn" disabled>Tag area</button>
      <button class="btn" disabled={!canEdit || busy} onclick={() => setStatus("Ignored")}>Mute</button>
      <button class="btn" onclick={copyLink}><Icon name="copy" size={14} />{copied ? "Copied" : "Copy link"}</button>
    </div>
  </header>

  <div class="cols">
    <div class="main-col">
      <Panel label="Occurrences · window">
        {#if buckets.some((b) => b > 0)}
          <div class="bars">
            {#each buckets as b, i (i)}
              <span class="barcol"><span class="barfill" class:regr={label === "Regressed"} style="height:{Math.max(2, (b / maxBucket) * 100)}%"></span></span>
            {/each}
          </div>
          <div class="axis mono muted"><span>window start</span><span>{int64(issue.count).toLocaleString()} total</span><span>now</span></div>
        {:else}
          <p class="muted small">No occurrences in this window.</p>
        {/if}
      </Panel>

      <Panel label="Correlation walk"><CorrelationWalk {projectId} current="issue" {issueId} errorType={issue.errorType ?? issue.title} /></Panel>

      <Panel label="Stack trace">
        {#if stack && stack.frames.length > 0}
          <div class="st-head">
            <span class="pill {stack.symbolicated ? 'res' : 'muted-pill'}">{stack.symbolicated ? "Symbolicated" : "Not symbolicated"}</span>
            {#if stack.message}<span class="muted mono st-msg">{stack.message}</span>{/if}
            <button class="btn raw-btn" onclick={() => (showRaw = !showRaw)}>{showRaw ? "Frames" : "Raw"}</button>
          </div>
          {#if showRaw}
            <pre class="raw mono">{stack.raw}</pre>
          {:else}
            <div class="frames">
              {#each inAppFrames as f, i (i)}
                <div class="frame">
                  <span class="fn mono">{f.function}</span>
                  {#if f.file}<span class="loc mono muted">{f.file}{#if f.line}:{f.line}{/if}</span>{/if}
                </div>
              {/each}
              {#if frameworkFrames.length > 0}
                <button class="fw-toggle" onclick={() => (showFramework = !showFramework)}>{showFramework ? "Hide" : "Show"} {frameworkFrames.length} framework frame{frameworkFrames.length === 1 ? "" : "s"}</button>
                {#if showFramework}
                  {#each frameworkFrames as f, i (i)}
                    <div class="frame fw"><span class="fn mono">{f.function}</span>{#if f.file}<span class="loc mono muted">{f.file}{#if f.line}:{f.line}{/if}</span>{/if}</div>
                  {/each}
                {/if}
              {/if}
            </div>
          {/if}
        {:else}
          <p class="muted small">No stack trace captured on this issue's occurrences. Browser errors and exceptions with a captured <span class="mono">exception.stacktrace</span> show frames here.</p>
        {/if}
      </Panel>
    </div>

    <div class="side-col">
      <Panel label="Version history">
        <div class="vh">
          <div class="vrow"><span class="vdot first"></span><span>First seen</span><span class="mono">v{int64(issue.firstSeenVersionSequence)}</span></div>
          {#if issue.resolvedInVersionSequence != null}<div class="vrow"><span class="vdot res"></span><span>Resolved</span><span class="mono">v{int64(issue.resolvedInVersionSequence)}</span></div>{/if}
          <div class="vrow"><span class="vdot {label === 'Regressed' ? 'regr' : 'last'}"></span><span>{label === "Regressed" ? "Regressed" : "Last seen"}</span><span class="mono">v{int64(issue.lastSeenVersionSequence)}</span></div>
        </div>
        {#if issue.affectedVersionSequences && issue.affectedVersionSequences.length > 0}
          <p class="muted small">Affected: {issue.affectedVersionSequences.map((v) => "v" + int64(v)).join(", ")}</p>
        {/if}
      </Panel>

      <Panel label="Details">
        <dl class="details">
          <dt class="muted">Service</dt><dd class="mono">{services.name(projectId, issue.serviceId)}</dd>
          <dt class="muted">Events</dt><dd class="mono">{int64(issue.count).toLocaleString()}</dd>
          <dt class="muted">Sessions</dt><dd class="mono">{series?.sessions ?? "—"}</dd>
          <dt class="muted">Fingerprint</dt><dd class="mono fp">{issue.fingerprint}</dd>
          <dt class="muted">Issue id</dt><dd class="mono fp">{issue.id}</dd>
        </dl>
      </Panel>
    </div>
  </div>
{/if}

<style>
  .crumbs { display: flex; align-items: center; gap: var(--gap-1); }
  .head { display: flex; justify-content: space-between; gap: var(--gap-4); flex-wrap: wrap; align-items: flex-start; }
  .titles { display: flex; flex-direction: column; gap: var(--gap-2); min-width: 0; }
  .titles h1 { margin: 0; }
  .pills { display: flex; gap: var(--gap-2); align-items: center; }
  .msg { color: var(--text-2); word-break: break-word; }
  .actions { display: flex; gap: var(--gap-2); align-items: flex-start; flex-wrap: wrap; }
  .small { font-size: 12px; }

  .cols { display: flex; flex-wrap: wrap; gap: var(--gap-3); align-items: flex-start; }
  .main-col { flex: 3 1 520px; min-width: 0; display: flex; flex-direction: column; gap: var(--gap-3); }
  .side-col { flex: 1 1 260px; min-width: 0; display: flex; flex-direction: column; gap: var(--gap-3); }

  .bars { display: flex; align-items: flex-end; gap: 3px; height: 90px; }
  .barcol { flex: 1; display: flex; align-items: flex-end; height: 100%; }
  .barfill { width: 100%; background: var(--unres-fg); border-radius: 2px 2px 0 0; }
  .barfill.regr { background: var(--regr-fg); }
  .axis { display: flex; justify-content: space-between; margin-top: 6px; font-size: 11px; }

  .vh { display: flex; flex-direction: column; gap: var(--gap-2); }
  .vrow { display: grid; grid-template-columns: 14px 1fr auto; gap: var(--gap-2); align-items: center; }
  .vdot { width: 10px; height: 10px; border-radius: 50%; }
  .vdot.first { background: var(--unres-fg); }
  .vdot.res { background: var(--res-fg); }
  .vdot.regr { background: var(--regr-fg); }
  .vdot.last { background: var(--muted); }

  .details { display: grid; grid-template-columns: 90px 1fr; gap: var(--gap-2) var(--gap-3); margin: 0; }
  .details dd { margin: 0; }
  .fp { word-break: break-all; }

  .st-head { display: flex; align-items: center; gap: var(--gap-2); margin-bottom: var(--gap-3); flex-wrap: wrap; }
  .st-msg { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .raw-btn { margin-left: auto; }
  .raw { margin: 0; padding: var(--gap-3); background: var(--bg-sunken); border-radius: var(--r-ctl); overflow-x: auto; white-space: pre; color: var(--text-2); }
  .frames { display: flex; flex-direction: column; }
  .frame { display: flex; justify-content: space-between; gap: var(--gap-3); padding: 7px 10px; border-left: 2px solid var(--accent); background: var(--surface); border-radius: 0 var(--r-tag) var(--r-tag) 0; margin-bottom: 3px; }
  .frame.fw { border-left-color: var(--divider); opacity: 0.75; }
  .fn { color: var(--text); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .loc { white-space: nowrap; }
  .fw-toggle { background: none; border: 0; color: var(--accent); cursor: pointer; font: inherit; text-align: left; padding: 6px 0; }
</style>
