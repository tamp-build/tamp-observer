<script lang="ts">
  // Storage & health (README 7.8): the pipeline-health dashboard, matched to the design. Live data where the API
  // can source it (Valkey, Postgres, pipeline, KPIs); sections that need data we do not collect yet (throughput
  // and freshness history, per-pod CPU/memory, retention schedule, the health-event log) show an honest "not
  // collected yet" rather than fabricated numbers. Polls every 15s while the tab is visible.
  import { api } from "../../api/client";
  import type { components } from "../../api/schema";
  import LoadingState from "../../components/ui/LoadingState.svelte";
  import ErrorState from "../../components/ui/ErrorState.svelte";
  import Icon from "../../components/ui/Icon.svelte";
  import { bytes, int64 } from "../../format";

  type Health = components["schemas"]["HealthView"];

  let data = $state<Health | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let autoRefresh = $state(true);
  let checkedAt = $state<number>(Date.now());
  let tick = $state(0); // drives the "checked Ns ago" label

  const statusClass: Record<string, string> = { ok: "ok", warn: "warn", crit: "crit", off: "off", healthy: "ok", degraded: "warn", critical: "crit" };
  function pill(s: string): string {
    return statusClass[s] ?? "off";
  }
  const checkedAgo = $derived.by(() => {
    void tick;
    return Math.max(0, Math.round((Date.now() - checkedAt) / 1000));
  });

  async function load() {
    error = null;
    const { data: d, response } = await api.GET("/api/health/overview");
    if (d) {
      data = d;
      checkedAt = Date.now();
    } else {
      error = `${response.status} ${response.statusText}`;
    }
    loading = false;
  }

  $effect(() => {
    load();
    const poll = window.setInterval(() => {
      if (autoRefresh && document.visibilityState === "visible") load();
    }, 15000);
    const label = window.setInterval(() => (tick = Date.now()), 1000);
    return () => {
      window.clearInterval(poll);
      window.clearInterval(label);
    };
  });

  function copyDiagnostics() {
    try {
      navigator.clipboard.writeText(JSON.stringify(data, null, 2));
    } catch {
      /* clipboard blocked */
    }
  }
  function pct(used: number, max: number): number {
    return max > 0 ? Math.min(100, Math.round((used / max) * 100)) : 0;
  }
</script>

<header class="head">
  <div class="titles">
    <h1>Storage &amp; health</h1>
    <span class="muted">Instance-wide · every project shares this pipeline</span>
  </div>
  <div class="controls">
    <span class="muted small">Checked {checkedAgo}s ago</span>
    <label class="chk"><input type="checkbox" bind:checked={autoRefresh} /> Auto-refresh 15s</label>
    <button class="btn" onclick={copyDiagnostics}>Copy diagnostics</button>
  </div>
</header>

{#if loading}
  <LoadingState rows={8} />
{:else if error}
  <ErrorState message={`Could not load health (${error}).`} onretry={load} />
{:else if data}
  <!-- Overall status -->
  {#if data.overallStatus === "healthy"}
    <p class="all-ok"><span class="dot"></span> All systems healthy</p>
  {:else}
    <section class="banner {pill(data.overallStatus)}">
      <span class="pill {pill(data.overallStatus)}"><Icon name="alert" size={12} />{data.overallStatus === "critical" ? "Critical" : "Degraded"}</span>
      <p class="msg">{data.overallMessage}</p>
      <a class="btn" href="#streams">View stream</a>
    </section>
  {/if}

  <!-- KPI tiles -->
  <div class="tiles">
    <div class="tile"><span class="h">Ingest rate</span><span class="big">{data.kpis.ingestRatePerSec ?? "—"}<span class="unit"> events/s</span></span><span class="muted small">live rate not collected yet</span></div>
    <div class="tile"><span class="h">Freshness p95</span><span class="big">{data.kpis.freshnessP95Sec ?? "—"}<span class="unit"> s</span></span><span class="muted small">received → queryable · target under 10s</span></div>
    <div class="tile"><span class="h">Buffered in Valkey</span><span class="big">{int64(data.kpis.bufferedPending).toLocaleString()}<span class="unit"> pending</span></span><span class="small" style="color:var(--muted)">{data.kpis.dominantStream ? `in ${data.kpis.dominantStream}` : "drained"}</span></div>
    <div class="tile"><span class="h">Dropped · 24h</span><span class="big">{int64(data.kpis.dropped24h)}</span><span class="muted small">rejected {int64(data.kpis.rejected24h)} (bad project key)</span></div>
    <div class="tile"><span class="h">Dead letters</span><span class="big">{int64(data.kpis.deadLetters)}</span><span class="muted small">quarantined events</span></div>
  </div>

  <!-- Pipeline -->
  <section class="panel pad pipeline">
    <div class="row-between"><h2 class="h">Ingest pipeline · live</h2></div>
    <div class="stages">
      {#each data.pipeline as stage, i (stage.name)}
        <div class="stage {stage.status === 'warn' ? 'w' : stage.status === 'off' ? 'x' : ''}">
          <div class="stage-top">
            <strong>{stage.name}</strong>
            {#if stage.tag}<span class="tag">{stage.tag}</span>{/if}
            <span class="pill {pill(stage.status)}" style="margin-left:auto">{stage.status === "ok" ? "OK" : stage.status === "warn" ? "Lagging" : stage.status === "off" ? "Off" : "Down"}</span>
          </div>
          {#each stage.facts as f (f)}<span class="muted small">{f}</span>{/each}
        </div>
        {#if i < data.pipeline.length - 1}
          <div class="flow"><Icon name="chevron-right" size={16} /></div>
        {/if}
      {/each}
    </div>
  </section>

  <!-- Valkey -->
  <section id="streams" class="valkey">
    <div class="row-between"><h2 class="section-title">Valkey <span class="muted small">memory streams between the collector and the evaluator</span></h2>{#if data.valkey && int64(data.valkey.laggingCount) > 0}<span class="pill warn">{data.valkey.laggingCount} stream lagging</span>{/if}</div>
    {#if data.valkey}
      <div class="valkey-grid">
        <div class="panel pad server">
          <h3 class="h">Server</h3>
          <div class="usage">
            <div class="row-between"><span>Memory</span><span class="mono">{bytes(data.valkey.server.usedMemoryBytes)}{int64(data.valkey.server.maxMemoryBytes) > 0 ? ` of ${bytes(data.valkey.server.maxMemoryBytes)}` : ""}</span></div>
            {#if int64(data.valkey.server.maxMemoryBytes) > 0}<div class="bar"><span style="width:{pct(int64(data.valkey.server.usedMemoryBytes), int64(data.valkey.server.maxMemoryBytes))}%;background:var(--res-fg)"></span></div>{/if}
          </div>
          <div class="kvs">
            <div class="kv"><span class="muted">Eviction policy</span><span class="mono">{data.valkey.server.evictionPolicy}</span></div>
            <div class="kv"><span class="muted">Persistence</span><span class="mono">{data.valkey.server.persistence}</span></div>
            <div class="kv"><span class="muted">Ops/s</span><span class="mono">{int64(data.valkey.server.opsPerSec).toLocaleString()}</span></div>
            <div class="kv"><span class="muted">Clients</span><span class="mono">{data.valkey.server.clients}</span></div>
            <div class="kv"><span class="muted">Version · uptime</span><span class="mono">{data.valkey.server.version} · {Number(data.valkey.server.uptimeDays).toFixed(0)}d</span></div>
          </div>
        </div>
        <div class="panel streams">
          <div class="scroll-x"><div class="st-wrap">
            <div class="st th"><span>Stream</span><span class="num">Length</span><span class="num">Pending</span><span class="num">Consumers</span><span>Status</span></div>
            {#each data.valkey.streams as s (s.name)}
              <div class="st" class:warnrow={s.status === "warn"}>
                <span class="mono">{s.name}</span>
                <span class="num mono">{int64(s.length).toLocaleString()}</span>
                <span class="num mono" class:warntext={s.status === "warn"}>{int64(s.pending).toLocaleString()}</span>
                <span class="num mono">{s.consumers} of {s.consumersExpected}</span>
                <span><span class="pill {pill(s.status)}">{s.status === "ok" ? "OK" : "Lagging"}</span></span>
              </div>
            {/each}
          </div></div>
          <p class="muted small foot">Trimmed with <span class="mono">MAXLEN ~</span>. Pending = unacknowledged entries in the <span class="mono">evaluator</span> consumer group. In/s, out/s and lag-seconds need rate history (not collected yet).</p>
        </div>
      </div>
    {:else}
      <div class="panel pad"><p class="muted">Valkey is unreachable from the API.</p></div>
    {/if}
  </section>

  <!-- Storage tiers -->
  <section class="tiers">
    <h2 class="section-title">Storage tiers</h2>
    <div class="tier-grid">
      <div class="panel pad">
        <div class="tier-head"><strong>Postgres</strong><span class="tag">write store · system of record</span><span class="pill {data.postgres ? 'ok' : 'crit'}" style="margin-left:auto">{data.postgres ? "Healthy" : "Down"}</span></div>
        {#if data.postgres}
          <div class="usage"><div class="row-between"><span>Connections</span><span class="mono">{data.postgres.connections} of {data.postgres.maxConnections}</span></div><div class="bar"><span style="width:{pct(int64(data.postgres.connections), int64(data.postgres.maxConnections))}%;background:var(--res-fg)"></span></div></div>
          <div class="kvs">
            <div class="kv"><span class="muted">Database size</span><span class="mono">{bytes(data.postgres.dbSizeBytes)}</span></div>
            <div class="kv"><span class="muted">Cache hit ratio</span><span class="mono">{(Number(data.postgres.cacheHitRatio) * 100).toFixed(1)}%</span></div>
            <div class="kv"><span class="muted">Version</span><span class="mono">{data.postgres.version}</span></div>
          </div>
          <h3 class="h" style="margin-top:4px">Largest tables</h3>
          {#each data.postgres.largestTables as t (t.name)}
            <div class="table-bar"><span class="mono">{t.name}</span><div class="bar"><span style="width:{pct(int64(t.bytes), int64(data.postgres.largestTables[0]?.bytes ?? 1))}%;background:var(--accent)"></span></div><span class="mono num">{bytes(t.bytes)}</span></div>
          {/each}
        {/if}
      </div>
      <div class="panel pad">
        <div class="tier-head"><strong>DuckDB</strong><span class="tag">read store · in-process</span><span class="pill ok" style="margin-left:auto">Healthy</span></div>
        <div class="kvs">
          <div class="kv"><span class="muted">Mode</span><span class="mono">{data.duck.enabled ? "active (reads Postgres)" : "standby"}</span></div>
          <div class="kv"><span class="muted">Read store dial</span><span class="mono">{data.duck.store}</span></div>
          <div class="kv"><span class="muted">Copies</span><span>one per API instance</span></div>
          <div class="kv"><span class="muted">Query p95 · behind Postgres</span><span class="muted">not collected yet</span></div>
        </div>
      </div>
      <div class="panel pad dashed">
        <div class="tier-head"><strong class="muted2">ClickHouse</strong><span class="tag">analytical tier · optional</span><span class="pill {data.clickHouseConfigured ? 'ok' : 'off'}" style="margin-left:auto">{data.clickHouseConfigured ? "Configured" : "Not configured"}</span></div>
        <p class="dim">Not needed at this volume. Postgres and DuckDB are serving every query.</p>
        <p class="muted small">Consider it when DuckDB query times or Postgres growth become the bottleneck. It's switched on in deployment configuration, not from this page.</p>
      </div>
    </div>
  </section>

  <!-- Retention + Components -->
  <div class="two-col">
    <section class="panel pad">
      <h2 class="h">Retention &amp; disk</h2>
      <div class="usage" style="margin-top:8px"><div class="row-between"><span>Postgres volume (used)</span><span class="mono">{data.postgres ? bytes(data.postgres.dbSizeBytes) : "—"}</span></div></div>
      <p class="muted small">Per-type retention schedule and volume limits need the retention scheduler (TOBS-24), not collected yet.</p>
    </section>

    <section id="components" class="panel comp-panel">
      <div class="pad"><h2 class="h">Components</h2></div>
      <div class="scroll-x"><div class="cmp-wrap">
        <div class="cmp th"><span>Component</span><span class="num">Running</span><span>Status</span><span class="num">Memory</span></div>
        {#each data.components as c (c.name)}
          <div class="cmp">
            <span class="cname"><span>{c.name}</span><span class="mono muted tiny">{c.subtitle}</span></span>
            <span class="num mono">{c.runningDesired ?? "—"}</span>
            <span><span class="pill {pill(c.status)}">{c.status === "ok" ? "Healthy" : c.status === "warn" ? "Degraded" : "Down"}</span></span>
            <span class="num mono">{c.memoryBytes ? bytes(c.memoryBytes) : "—"}</span>
          </div>
        {/each}
      </div></div>
      <p class="muted small pad">CPU, restarts and uptime need the k8s metrics API (not wired yet).</p>
    </section>
  </div>

  <!-- Health events -->
  <section class="panel pad">
    <h2 class="h">Health events · last 24h</h2>
    <p class="muted small">A persisted health-event log (warnings, resolutions, purges, AOF rewrites) is not collected yet.</p>
  </section>
{/if}

<style>
  .head { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: var(--gap-3); }
  .titles { display: flex; flex-direction: column; gap: 2px; }
  .titles h1 { margin: 0; }
  .controls { display: flex; flex-wrap: wrap; gap: var(--gap-2); align-items: center; }
  .small { font-size: var(--fs-label); }
  .tiny { font-size: 11px; }
  .chk { display: inline-flex; align-items: center; gap: var(--gap-2); color: var(--text-2); }
  .pad { padding: var(--gap-4); }

  /* status pills (status palette only, always with a label) */
  .pill.ok { background: var(--res-bg); color: var(--res-fg); }
  .pill.warn { background: #33290f; color: var(--warn); }
  .pill.crit { background: var(--err-bg); color: var(--err); }
  .pill.off { background: var(--surface-2); color: var(--text-2); }

  .all-ok { display: flex; align-items: center; gap: var(--gap-2); color: var(--res-fg); margin: 0; }
  .all-ok .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--res-fg); }
  .banner { display: flex; flex-wrap: wrap; align-items: center; gap: var(--gap-3) var(--gap-4); padding: var(--gap-3) var(--gap-4); border: 1px solid; border-radius: var(--r-panel); }
  .banner.warn { border-color: #5c4a1a; background: #1a160b; }
  .banner.crit { border-color: var(--err-outline); background: var(--err-bg); }
  .banner .msg { margin: 0; flex: 1 1 420px; color: var(--text); }

  .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(200px, 100%), 1fr)); gap: var(--gap-3); }
  .tile { display: flex; flex-direction: column; gap: var(--gap-1); padding: 14px 16px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--r-panel); }
  .big { font-size: 24px; font-weight: 650; letter-spacing: -0.01em; font-variant-numeric: tabular-nums; }
  .unit { font-size: 13px; font-weight: 500; color: var(--muted); }

  .row-between { display: flex; justify-content: space-between; gap: var(--gap-3); flex-wrap: wrap; align-items: baseline; }
  .pipeline { display: flex; flex-direction: column; gap: var(--gap-3); }
  .stages { display: flex; flex-wrap: wrap; align-items: stretch; gap: var(--gap-1); }
  .stage { flex: 1 1 130px; min-width: 0; display: flex; flex-direction: column; gap: 6px; padding: 12px 14px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--r-panel); }
  .stage.w { border-color: #5c4a1a; background: #1a160b; }
  .stage.x { border-style: dashed; background: transparent; }
  .stage-top { display: flex; align-items: center; gap: 6px; }
  .flow { display: flex; align-items: center; color: var(--muted); }

  .bar { height: 6px; border-radius: 3px; background: var(--table-bg); overflow: hidden; }
  .bar span { display: block; height: 6px; border-radius: 3px; }
  .usage { display: flex; flex-direction: column; gap: 6px; }
  .kvs { display: flex; flex-direction: column; }
  .kv { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: var(--gap-3); padding: 6px 0; border-top: 1px solid var(--divider); align-items: baseline; }

  .section-title { margin: 0; font-size: 16px; }
  .valkey { display: flex; flex-direction: column; gap: var(--gap-3); }
  .valkey-grid { display: flex; flex-wrap: wrap; gap: var(--gap-3); align-items: flex-start; }
  .server { flex: 1 1 280px; min-width: 0; display: flex; flex-direction: column; gap: var(--gap-2); }
  .streams { flex: 999 1 560px; min-width: 0; overflow: hidden; }
  .st-wrap { min-width: 560px; }
  .st { display: grid; grid-template-columns: minmax(160px, 1.5fr) 100px 100px 110px 90px; gap: var(--gap-3); align-items: center; padding: 9px 14px; border-top: 1px solid var(--divider); }
  .st.th { border-top: 0; padding: 8px 14px; font-size: var(--fs-label); color: var(--muted); text-transform: uppercase; letter-spacing: 0.06em; font-weight: 600; }
  .warnrow { background: #1a160b; }
  .warntext { color: var(--warn); }
  .foot { margin: 0; padding: 10px 14px; border-top: 1px solid var(--divider); }

  .tiers { display: flex; flex-direction: column; gap: var(--gap-3); }
  .tier-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(320px, 100%), 1fr)); gap: var(--gap-3); }
  .tier-head { display: flex; align-items: center; gap: var(--gap-2); }
  .tier-head strong { font-size: 15px; }
  .dashed { border-style: dashed; background: transparent; }
  .muted2 { color: var(--text-2); }
  .dim { margin: 0; color: var(--text-2); }
  .table-bar { display: grid; grid-template-columns: 110px minmax(0, 1fr) 70px; gap: var(--gap-2); align-items: center; }

  .two-col { display: flex; flex-wrap: wrap; gap: var(--gap-3); align-items: flex-start; }
  .two-col > * { flex: 1 1 400px; min-width: 0; }
  .comp-panel { overflow: hidden; }
  .cmp-wrap { min-width: 520px; }
  .cmp { display: grid; grid-template-columns: minmax(140px, 1.4fr) 90px 110px 100px; gap: var(--gap-3); align-items: center; padding: 9px 14px; border-top: 1px solid var(--divider); }
  .cmp.th { border-top: 0; padding: 8px 14px; font-size: var(--fs-label); color: var(--muted); text-transform: uppercase; letter-spacing: 0.06em; font-weight: 600; }
  .cname { display: flex; flex-direction: column; }

  .tile .big, .big { line-height: 1.1; }
</style>
