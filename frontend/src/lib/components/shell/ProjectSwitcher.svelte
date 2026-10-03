<script lang="ts">
  // Lists only the projects the viewer can access (README section 4). Switching carries the current global
  // filters forward via preserveQuery.
  import { instance } from "../../stores/instance.svelte";
  import { router } from "../../router.svelte";
  import Icon from "../ui/Icon.svelte";

  interface Props {
    currentProjectId?: string;
  }
  let { currentProjectId }: Props = $props();

  let open = $state(false);

  const current = $derived(currentProjectId ? instance.project(currentProjectId) : undefined);

  function pick(id: string) {
    open = false;
    router.navigate(router.projectHref(id, "/issues"), { preserveQuery: true });
  }
</script>

<div class="wrap">
  <button class="btn switch" onclick={() => (open = !open)}>
    {current ? current.name : "Select project"}
    <Icon name="chevron-down" size={14} />
  </button>
  {#if open}
    <div class="panel pop">
      {#if instance.projects.length === 0}
        <p class="muted empty">No projects you can access.</p>
      {:else}
        {#each instance.projects as p (p.id)}
          <button class="item" class:on={p.id === currentProjectId} onclick={() => pick(p.id)}>
            <span>{p.name}</span>
            <span class="mono muted">{p.key}</span>
          </button>
        {/each}
      {/if}
    </div>
  {/if}
</div>

<style>
  .wrap {
    position: relative;
  }
  .switch {
    max-width: 240px;
  }
  .pop {
    position: absolute;
    left: 0;
    top: calc(100% + 6px);
    min-width: 260px;
    padding: var(--gap-1);
    display: flex;
    flex-direction: column;
    gap: 1px;
    z-index: 20;
  }
  .item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap-3);
    padding: var(--gap-2) var(--gap-3);
    border: 0;
    border-radius: var(--r-ctl);
    background: none;
    color: var(--text-nav);
    font: inherit;
    cursor: pointer;
    text-align: left;
  }
  .item:hover {
    background: var(--surface-hover);
    color: var(--text);
  }
  .item.on {
    background: var(--surface-sel);
    color: var(--text);
    font-weight: 600;
  }
  .empty {
    padding: var(--gap-2) var(--gap-3);
    margin: 0;
  }
</style>
