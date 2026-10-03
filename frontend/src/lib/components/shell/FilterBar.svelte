<script lang="ts">
  // Per-surface filter bar (README section 4): environment segmented control, time window, version. Global
  // state in the filters store, round-tripped through the URL query string.
  import { filters, WINDOWS, type TimeWindow } from "../../stores/filters.svelte";

  // A versions endpoint is a gap; until it exists the version picker is a free-text filter (noted follow-up).
  const ENVS = ["prod", "staging", "qa", "dev"];

  let versionInput = $state(filters.version ?? "");

  function applyVersion(e: Event) {
    e.preventDefault();
    filters.setVersion(versionInput.trim() || null);
  }
</script>

<div class="filter-bar">
  <div class="seg">
    {#each ENVS as env (env)}
      <button class:on={filters.env === env} onclick={() => filters.setEnv(env)}>{env}</button>
    {/each}
  </div>

  <div class="seg">
    {#each WINDOWS as w (w)}
      <button class:on={filters.window === w} onclick={() => filters.setWindow(w as TimeWindow)}>{w}</button>
    {/each}
  </div>

  <form class="field version" onsubmit={applyVersion}>
    <input bind:value={versionInput} placeholder="version" aria-label="Version filter" />
  </form>
</div>

<style>
  .filter-bar {
    display: flex;
    flex-wrap: wrap;
    gap: var(--gap-2);
    align-items: center;
  }
  .version {
    width: 120px;
  }
</style>
