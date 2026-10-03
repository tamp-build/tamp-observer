<script lang="ts">
  // Storage & health (README section 9). GET /api/health/storage: which engines the dial selected for the
  // write and read paths (ADR 0005/0006), and whether the ClickHouse analytical tier is configured.
  import { api } from "../../api/client";
  import type { components } from "../../api/schema";
  import LoadingState from "../../components/ui/LoadingState.svelte";
  import ErrorState from "../../components/ui/ErrorState.svelte";
  import Panel from "../../components/ui/Panel.svelte";

  type Health = components["schemas"]["StorageHealth"];

  let health = $state<Health | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function load() {
    loading = true;
    error = null;
    const { data, response } = await api.GET("/api/health/storage");
    if (data) health = data;
    else error = `${response.status} ${response.statusText}`;
    loading = false;
  }

  $effect(() => {
    load();
  });
</script>

<header class="surface-head"><h1>Storage & health</h1></header>

{#if loading}
  <LoadingState rows={3} />
{:else if error}
  <ErrorState message={`Could not load storage health (${error}).`} onretry={load} />
{:else if health}
  <Panel label="Storage tiers">
    <dl class="grid">
      <dt class="muted">Write store</dt>
      <dd class="mono">{health.writeStore}</dd>
      <dt class="muted">Read store</dt>
      <dd class="mono">{health.readStore}</dd>
      <dt class="muted">ClickHouse tier</dt>
      <dd>
        <span class="pill {health.clickHouseConfigured ? 'res' : 'muted-pill'}">
          {health.clickHouseConfigured ? "Configured" : "Not configured"}
        </span>
      </dd>
    </dl>
  </Panel>
{/if}

<style>
  .surface-head h1 {
    margin: 0;
  }
  .grid {
    display: grid;
    grid-template-columns: 160px 1fr;
    gap: var(--gap-3) var(--gap-4);
    margin: 0;
    align-items: center;
  }
  .grid dd {
    margin: 0;
  }
</style>
