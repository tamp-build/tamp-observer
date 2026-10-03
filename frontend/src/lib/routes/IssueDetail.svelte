<script lang="ts">
  // Issue detail (README 7.2), wired to GET /api/projects/{id}/issues/{issueId}. The full correlation walk,
  // stack trace and breadcrumbs are follow-ups once those endpoints land; this foundation covers the header,
  // status, lifecycle, and the role-gated status actions (POST .../status, EditCapturePolicy).
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import { session } from "../stores/session.svelte";
  import StatusPill from "../components/ui/StatusPill.svelte";
  import LoadingState from "../components/ui/LoadingState.svelte";
  import ErrorState from "../components/ui/ErrorState.svelte";
  import Panel from "../components/ui/Panel.svelte";
  import Icon from "../components/ui/Icon.svelte";
  import CorrelationWalk from "../components/CorrelationWalk.svelte";
  import { issueStatusLabel, int64, timeAgo } from "../format";
  import { guard, timeout } from "../net";

  interface Props {
    projectId: string;
    issueId: string;
  }
  let { projectId, issueId }: Props = $props();

  type Issue = components["schemas"]["Issue"];

  let issue = $state<Issue | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let busy = $state(false);

  const canEdit = $derived(session.can("EditCapturePolicy", { project: projectId }));
  const isResolved = $derived(issue?.status === 1);

  async function load() {
    loading = true;
    error = null;
    error = await guard("load issue", async () => {
      const { data, response } = await api.GET("/api/projects/{projectId}/issues/{issueId}", {
        params: { path: { projectId, issueId } },
        ...timeout(),
      });
      if (data) issue = data;
      return response;
    });
    loading = false;
  }

  async function setStatus(status: string) {
    if (!canEdit) return;
    busy = true;
    const failure = await guard("update issue status", async () => {
      const { response } = await api.POST("/api/projects/{projectId}/issues/{issueId}/status", {
        params: { path: { projectId, issueId } },
        body: { status, resolvedInVersionSequence: null },
        ...timeout(),
      });
      return response;
    });
    busy = false;
    if (!failure) await load();
  }

  $effect(() => {
    void issueId;
    load();
  });
</script>

<nav class="crumbs mono muted">
  <a href={router.projectHref(projectId, "/issues")} use:link data-keep-filters="true">Issues</a>
  <Icon name="chevron-right" size={12} />
  <span>{issueId.slice(0, 8)}</span>
</nav>

{#if loading}
  <LoadingState rows={4} />
{:else if error}
  <ErrorState message={`Could not load issue (${error}).`} onretry={load} />
{:else if issue}
  <header class="head">
    <div class="titles">
      <div class="pills">
        <StatusPill status={issueStatusLabel(issue.status)} version={int64(issue.lastSeenVersionSequence).toString()} />
      </div>
      <h1>{issue.errorType ?? "Issue"}</h1>
      <p class="mono msg">{issue.title}</p>
      <p class="muted">
        First seen {timeAgo(issue.firstSeenAtUtc)} · last seen {timeAgo(issue.lastSeenAtUtc)} ·
        versions {int64(issue.firstSeenVersionSequence)} → {int64(issue.lastSeenVersionSequence)}
      </p>
    </div>
    <div class="actions">
      {#if isResolved}
        <button class="btn" disabled={!canEdit || busy} onclick={() => setStatus("Unresolved")}>Reopen</button>
      {:else}
        <button class="btn pri" disabled={!canEdit || busy} onclick={() => setStatus("Resolved")}>
          <Icon name="check" size={14} />Resolve
        </button>
      {/if}
      <button class="btn" disabled={!canEdit || busy} onclick={() => setStatus("Ignored")}>Mute</button>
    </div>
  </header>

  <Panel label="Details">
    <dl class="details">
      <dt class="muted">Fingerprint</dt>
      <dd class="mono">{issue.fingerprint}</dd>
      <dt class="muted">Events</dt>
      <dd class="mono">{int64(issue.count).toLocaleString()}</dd>
      <dt class="muted">Issue id</dt>
      <dd class="mono">{issue.id}</dd>
    </dl>
  </Panel>

  <Panel label="Correlation walk">
    <CorrelationWalk {projectId} {issueId} errorType={issue.errorType ?? issue.title} />
  </Panel>
{/if}

<style>
  .crumbs {
    display: flex;
    align-items: center;
    gap: var(--gap-1);
  }
  .head {
    display: flex;
    justify-content: space-between;
    gap: var(--gap-4);
    flex-wrap: wrap;
  }
  .titles {
    display: flex;
    flex-direction: column;
    gap: var(--gap-2);
    min-width: 0;
  }
  .msg {
    color: var(--text-2);
    word-break: break-word;
  }
  .actions {
    display: flex;
    gap: var(--gap-2);
    align-items: flex-start;
  }
  .details {
    display: grid;
    grid-template-columns: 140px 1fr;
    gap: var(--gap-2) var(--gap-4);
    margin: 0;
  }
  .details dd {
    margin: 0;
    word-break: break-all;
  }
</style>
