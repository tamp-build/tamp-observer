<script lang="ts">
  interface Props {
    message: string;
    onretry?: () => void;
  }
  let { message, onretry }: Props = $props();
</script>

<div class="err">
  <p>{message}</p>
  {#if onretry}
    <button class="btn" onclick={onretry}>Retry</button>
  {/if}
</div>

<style>
  .err {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: var(--gap-3);
    padding: var(--gap-5) var(--gap-4);
    text-align: center;
    color: var(--err);
  }
</style>
