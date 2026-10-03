<script lang="ts">
  // The correlation walk (README 7.2): four linked cards threading Error -> Trace -> Logs-on-trace -> Session
  // replay for the latest occurrence. Reusable and directional: the `current` card is the surface you are on
  // (highlighted, not a link); the others link across. Resolvable from any anchor (issue, trace or session), so
  // the same component drives the walk on the issue, trace and replay pages. Missing links render disabled with
  // the reason, never hidden.
  import { api } from "../api/client";
  import type { components } from "../api/schema";
  import { router, link } from "../router.svelte";
  import LoadingState from "./ui/LoadingState.svelte";
  import Icon from "./ui/Icon.svelte";

  type Anchor = "issue" | "trace" | "replay";

  interface Props {
    projectId: string;
    current: Anchor;
    issueId?: string;
    traceId?: string;
    sessionId?: string;
    errorType?: string | null;
  }
  let { projectId, current, issueId, traceId, sessionId, errorType }: Props = $props();

  type Correlation = components["schemas"]["CorrelationView"];

  let data = $state<Correlation | null>(null);
  let loading = $state(true);

  const p = router.projectHref.bind(router);
  function ms(v: number | string | undefined): string {
    const n = typeof v === "string" ? Number(v) : (v ?? 0);
    return (n / 1_000_000).toFixed(1) + " ms";
  }
  function num(v: number | string | undefined): number {
    return typeof v === "string" ? Number(v) : (v ?? 0);
  }

  async function load() {
    loading = true;
    let d: Correlation | undefined;
    if (current === "issue" && issueId) {
      ({ data: d } = await api.GET("/api/projects/{projectId}/issues/{issueId}/correlation", {
        params: { path: { projectId, issueId } },
      }));
    } else if (current === "trace" && traceId) {
      ({ data: d } = await api.GET("/api/projects/{projectId}/traces/{traceId}/correlation", {
        params: { path: { projectId, traceId } },
      }));
    } else if (current === "replay" && sessionId) {
      ({ data: d } = await api.GET("/api/projects/{projectId}/sessions/{sessionId}/correlation", {
        params: { path: { projectId, sessionId } },
      }));
    }
    data = d ?? null;
    loading = false;
  }

  $effect(() => {
    void [current, issueId, traceId, sessionId];
    load();
  });

  const errorLabel = $derived(data?.errorType ?? errorType ?? "Error");
</script>

{#if loading}
  <LoadingState rows={2} />
{:else if data}
  <div class="walk">
    <!-- 1 Error -->
    {#if current !== "issue" && data.issueId}
      <a class="card linked" class:current={false} href={p(projectId, `/issues/${data.issueId}`)} use:link data-keep-filters="true">
        <div class="step"><span class="n err-n">1</span> Error</div>
        <div class="title">{errorLabel}</div>
        <div class="muted sub">Open issue</div>
      </a>
    {:else}
      <div class="card error" class:current={current === "issue"}>
        <div class="step"><span class="n err-n">1</span> Error</div>
        <div class="title">{errorLabel}</div>
        <div class="muted sub">{current === "issue" ? "Latest occurrence" : "No issue linked"}</div>
      </div>
    {/if}

    <div class="arrow"><Icon name="chevron-right" size={16} /></div>

    <!-- 2 Trace -->
    {#if data.traceId && current !== "trace"}
      <a class="card linked" href={p(projectId, `/traces/${data.traceId}`)} use:link data-keep-filters="true">
        <div class="step"><span class="n">2</span> Trace</div>
        {#if data.trace}
          <div class="title mono">{data.trace.rootOperation}</div>
          <div class="muted sub">{ms(data.trace.durationNano)} · {num(data.trace.spanCount)} spans · {num(data.trace.serviceCount)} svc{#if num(data.trace.errorSpanCount) > 0} · <span class="err">{num(data.trace.errorSpanCount)} err</span>{/if}</div>
        {:else}
          <div class="title mono">{data.traceId.slice(0, 16)}…</div>
          <div class="muted sub">Open trace</div>
        {/if}
      </a>
    {:else if data.traceId}
      <div class="card current">
        <div class="step"><span class="n">2</span> Trace</div>
        {#if data.trace}
          <div class="title mono">{data.trace.rootOperation}</div>
          <div class="muted sub">{ms(data.trace.durationNano)} · {num(data.trace.spanCount)} spans · {num(data.trace.serviceCount)} svc</div>
        {:else}
          <div class="title mono">{data.traceId.slice(0, 16)}…</div>
        {/if}
      </div>
    {:else}
      <div class="card disabled">
        <div class="step"><span class="n">2</span> Trace</div>
        <div class="muted sub">No trace linked to this occurrence</div>
      </div>
    {/if}

    <div class="arrow"><Icon name="chevron-right" size={16} /></div>

    <!-- 3 Logs on trace -->
    {#if data.logs && data.traceId}
      <a class="card linked" href={p(projectId, `/traces/${data.traceId}#logs`)} use:link data-keep-filters="true">
        <div class="step"><span class="n">3</span> Logs on trace</div>
        <div class="title">{num(data.logs.total)} logs{#if num(data.logs.errorCount) > 0} · <span class="err">{num(data.logs.errorCount)} err</span>{/if}{#if num(data.logs.warnCount) > 0} · <span class="warn">{num(data.logs.warnCount)} warn</span>{/if}</div>
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
    {#if data.replay?.available && current !== "replay"}
      <a class="card linked replay" href={p(projectId, `/replay/${data.replay.sessionId}`)} use:link data-keep-filters="true">
        <div class="step"><span class="n">4</span> Session replay</div>
        <div class="title mono">{data.replay.sessionId}</div>
        <div class="open"><Icon name="play" size={13} /> Open replay · {num(data.replay.eventCount)} events</div>
      </a>
    {:else if data.replay?.available}
      <div class="card current">
        <div class="step"><span class="n">4</span> Session replay</div>
        <div class="title mono">{data.replay.sessionId}</div>
        <div class="muted sub">{num(data.replay.eventCount)} events</div>
      </div>
    {:else}
      <div class="card disabled">
        <div class="step"><span class="n">4</span> Session replay</div>
        <div class="muted sub">
          {#if data.sessionId}Session {data.sessionId.slice(0, 8)}… not retained{:else}No session for this occurrence{/if}
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
  .card.current {
    border-color: var(--accent);
    box-shadow: inset 0 0 0 1px var(--accent);
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
  .err-n {
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
