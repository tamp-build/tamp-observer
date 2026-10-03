<script lang="ts">
  // The correlation walk (README 7.2): four linked cards for the latest occurrence of an issue, threading
  // Error -> Trace -> Logs-on-trace -> Session replay. Missing links render disabled with the reason, never
  // hidden, so the absence of a trace/session is itself information.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import LoadingState from "./ui/LoadingState.svelte";
  import Icon from "./ui/Icon.svelte";

  interface Props {
    projectId: string;
    issueId: string;
    errorType?: string | null;
  }
  let { projectId, issueId, errorType }: Props = $props();

  type Correlation = components["schemas"]["CorrelationView"];

  let data = $state<Correlation | null>(null);
  let loading = $state(true);

  function ms(nanos: number | string | undefined): string {
    const n = typeof nanos === "string" ? Number(nanos) : (nanos ?? 0);
    return (n / 1_000_000).toFixed(1) + " ms";
  }
  function n(v: number | string | undefined): number {
    return typeof v === "string" ? Number(v) : (v ?? 0);
  }

  async function load() {
    loading = true;
    const { data: d } = await api.GET("/api/projects/{projectId}/issues/{issueId}/correlation", {
      params: { path: { projectId, issueId } },
    });
    data = d ?? null;
    loading = false;
  }

  $effect(() => {
    void issueId;
    load();
  });
</script>

{#if loading}
  <LoadingState rows={2} />
{:else if data}
  <div class="walk">
    <!-- 1 Error -->
    <div class="card error">
      <div class="step"><span class="n">1</span> Error</div>
      <div class="title">{errorType ?? "Error"}</div>
      <div class="muted sub">Latest occurrence</div>
    </div>

    <div class="arrow"><Icon name="chevron-right" size={16} /></div>

    <!-- 2 Trace -->
    {#if data.traceId}
      <a class="card linked" href={router.projectHref(projectId, `/traces/${data.traceId}`)} use:link data-keep-filters="true">
        <div class="step"><span class="n">2</span> Trace</div>
        {#if data.trace}
          <div class="title mono">{data.trace.rootOperation}</div>
          <div class="muted sub">{ms(data.trace.durationNano)} · {n(data.trace.spanCount)} spans · {n(data.trace.serviceCount)} svc{#if n(data.trace.errorSpanCount) > 0} · <span class="err">{n(data.trace.errorSpanCount)} errors</span>{/if}</div>
        {:else}
          <div class="title mono">{data.traceId.slice(0, 16)}…</div>
          <div class="muted sub">Open trace</div>
        {/if}
      </a>
    {:else}
      <div class="card disabled">
        <div class="step"><span class="n">2</span> Trace</div>
        <div class="muted sub">No trace linked to this occurrence</div>
      </div>
    {/if}

    <div class="arrow"><Icon name="chevron-right" size={16} /></div>

    <!-- 3 Logs on trace -->
    {#if data.logs}
      <a class="card linked" href={router.projectHref(projectId, `/traces/${data.traceId}#logs`)} use:link data-keep-filters="true">
        <div class="step"><span class="n">3</span> Logs on trace</div>
        <div class="title">{n(data.logs.total)} logs{#if n(data.logs.errorCount) > 0} · <span class="err">{n(data.logs.errorCount)} err</span>{/if}{#if n(data.logs.warnCount) > 0} · <span class="warn">{n(data.logs.warnCount)} warn</span>{/if}</div>
        <div class="muted sub mono">{data.logs.topMessage ?? ""}</div>
      </a>
    {:else}
      <div class="card disabled">
        <div class="step"><span class="n">3</span> Logs on trace</div>
        <div class="muted sub">No logs on this trace</div>
      </div>
    {/if}

    <div class="arrow"><Icon name="chevron-right" size={16} /></div>

    <!-- 4 Session replay -->
    {#if data.replay?.available}
      <a class="card linked replay" href={router.projectHref(projectId, `/replay/${data.replay.sessionId}`)} use:link data-keep-filters="true">
        <div class="step"><span class="n">4</span> Session replay</div>
        <div class="title mono">{data.replay.sessionId}</div>
        <div class="open"><Icon name="play" size={13} /> Open replay · {n(data.replay.eventCount)} events</div>
      </a>
    {:else}
      <div class="card disabled">
        <div class="step"><span class="n">4</span> Session replay</div>
        <div class="muted sub">
          {#if data.sessionId}Session {data.sessionId.slice(0, 8)}… not retained{:else}No session for this occurrence (server job, or identity off){/if}
        </div>
      </div>
    {/if}
  </div>

  {#if data.sessionId}
    <p class="caption muted">Linked by session id <span class="mono">{data.sessionId}</span>, minted in the browser and stamped on the request.</p>
  {/if}
{/if}

<style>
  .walk {
    display: flex;
    align-items: stretch;
    gap: var(--gap-2);
    flex-wrap: wrap;
  }
  .card {
    flex: 1;
    min-width: 170px;
    display: flex;
    flex-direction: column;
    gap: var(--gap-1);
    padding: var(--gap-3);
    border: 1px solid var(--border);
    border-radius: var(--r-panel);
    background: var(--surface);
    color: var(--text);
  }
  .card.error {
    border-color: var(--regr-border);
    background: var(--regr-wash);
  }
  .card.linked:hover {
    border-color: var(--accent);
  }
  .card.disabled {
    border-style: dashed;
    color: var(--disabled);
    background: transparent;
  }
  .step {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    font-size: var(--fs-label);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
    font-weight: 600;
  }
  .n {
    display: inline-grid;
    place-items: center;
    width: 16px;
    height: 16px;
    border-radius: 50%;
    background: var(--surface-sel);
    color: var(--text-2);
    font-size: 10px;
  }
  .card.error .n {
    background: var(--regr-fg);
    color: var(--on-accent);
  }
  .title {
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .sub {
    font-size: var(--fs-label);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .open {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-1);
    color: var(--accent);
    font-size: var(--fs-label);
    font-weight: 600;
  }
  .arrow {
    display: flex;
    align-items: center;
    color: var(--muted);
  }
  .err {
    color: var(--err);
  }
  .warn {
    color: var(--warn);
  }
  .caption {
    font-size: var(--fs-label);
    margin: var(--gap-3) 0 0;
  }
  @media (max-width: 760px) {
    .arrow {
      transform: rotate(90deg);
      justify-content: center;
      width: 100%;
    }
  }
</style>
