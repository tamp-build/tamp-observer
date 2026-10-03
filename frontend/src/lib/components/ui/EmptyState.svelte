<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    /** One line saying why it is empty plus, ideally, the next action (README section 8). */
    message: string;
    action?: Snippet;
  }
  let { message, action }: Props = $props();
</script>

<div class="empty">
  <p class="muted">{message}</p>
  {#if action}{@render action()}{/if}
</div>

<style>
  .empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: var(--gap-3);
    padding: var(--gap-5) var(--gap-4);
    text-align: center;
  }
</style>
