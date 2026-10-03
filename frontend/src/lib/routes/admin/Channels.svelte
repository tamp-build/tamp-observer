<script lang="ts">
  // Notification channels (README 7.5). GET /api/channels, Admin only. A channel that is reachback (reaches an
  // outside host) and forbidden by the current enforcement mode is shown disabled with the reason, never hidden.
  import { api } from "../../api/client";
  import type { components } from "../../api/schema";
  import { instance } from "../../stores/instance.svelte";
  import { router } from "../../router.svelte";
  import LoadingState from "../../components/ui/LoadingState.svelte";
  import ErrorState from "../../components/ui/ErrorState.svelte";
  import LockedReason from "../../components/access/LockedReason.svelte";
  import { guard, timeout } from "../../net";

  type Channel = components["schemas"]["ChannelView"];

  let channels = $state<Channel[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  const mode = $derived(instance.enforcement?.mode ?? "advisory");

  async function load() {
    loading = true;
    error = null;
    error = await guard("load channels", async () => {
      const { data, response } = await api.GET("/api/channels", { ...timeout() });
      if (data) channels = data;
      return response;
    });
    loading = false;
  }

  $effect(() => {
    load();
  });
</script>

<header class="surface-head"><h1>Notification channels</h1></header>

{#if mode !== "advisory"}
  <div class="panel posture">
    <LockedReason reason={`Mode is ${mode}: channels that reach an outside host are refused. In-enclave channels stay available.`} />
    <a href="/admin/enforcement" onclick={(e) => { e.preventDefault(); router.navigate('/admin/enforcement'); }}>Enforcement</a>
  </div>
{/if}

{#if loading}
  <LoadingState rows={3} />
{:else if error}
  <ErrorState message={`Could not load channels (${error}).`} onretry={load} />
{:else}
  <div class="cards">
    {#each channels as ch (ch.type)}
      <div class="panel card" class:off={!ch.allowedUnderMode}>
        <header class="card-head">
          <h2 class="name">{ch.type}</h2>
          {#if ch.allowedUnderMode}
            <span class="pill res">Available</span>
          {:else}
            <span class="pill enf">Unavailable</span>
          {/if}
        </header>
        <p class="muted kind">{ch.reachback ? "Reaches an outside host (reachback)." : "In-enclave delivery."}</p>
        {#if !ch.allowedUnderMode}
          <LockedReason reason={`Refused under ${mode} mode.`} />
        {/if}
      </div>
    {/each}
  </div>
{/if}

<style>
  .surface-head h1 {
    margin: 0;
  }
  .posture {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap-3);
    padding: var(--gap-3) var(--gap-4);
    background: var(--enf-wash);
    border-color: var(--enf-border);
  }
  .cards {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
    gap: var(--gap-3);
  }
  .card {
    padding: var(--gap-4);
    display: flex;
    flex-direction: column;
    gap: var(--gap-2);
  }
  .card.off {
    opacity: 0.8;
  }
  .card-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  .name {
    font-size: 15px;
    margin: 0;
    text-transform: capitalize;
  }
  .kind {
    margin: 0;
  }
</style>
