<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    label?: string;
    pad?: boolean;
    children: Snippet;
    actions?: Snippet;
  }
  let { label, pad = true, children, actions }: Props = $props();
</script>

<section class="panel">
  {#if label || actions}
    <header class="panel-head">
      {#if label}<h2 class="h">{label}</h2>{/if}
      {#if actions}<div class="panel-actions">{@render actions()}</div>{/if}
    </header>
  {/if}
  <div class:panel-body={pad}>
    {@render children()}
  </div>
</section>

<style>
  .panel-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap-3);
    padding: var(--gap-3) var(--gap-4);
    border-bottom: 1px solid var(--divider);
  }
  .panel-actions {
    display: flex;
    gap: var(--gap-2);
  }
  .panel-body {
    padding: var(--gap-4);
  }
</style>
