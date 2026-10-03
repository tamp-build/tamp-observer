<script lang="ts">
  // A dismissible toast for the most recent captured error, so failures are visible rather than silent. Sits
  // above everything; async/load/render errors all land here through the errors store.
  import { errors } from "../../stores/errors.svelte";
  import Icon from "./Icon.svelte";
</script>

{#if errors.last}
  <div class="toast" role="alert">
    <span class="ic"><Icon name="alert" size={16} /></span>
    <div class="body">
      <span class="ctx">{errors.last.context}</span>
      <span class="msg">{errors.last.message}</span>
    </div>
    <button class="x" aria-label="Dismiss" onclick={() => errors.dismiss()}>×</button>
  </div>
{/if}

<style>
  .toast {
    position: fixed;
    bottom: var(--gap-4);
    right: var(--gap-4);
    z-index: 100;
    display: flex;
    align-items: flex-start;
    gap: var(--gap-2);
    max-width: 420px;
    padding: var(--gap-3);
    border: 1px solid var(--err-outline);
    border-radius: var(--r-panel);
    background: var(--err-bg);
    color: var(--text);
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  }
  .ic {
    color: var(--err);
    flex-shrink: 0;
  }
  .body {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .ctx {
    font-size: var(--fs-label);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--err);
    font-weight: 600;
  }
  .msg {
    word-break: break-word;
  }
  .x {
    background: none;
    border: 0;
    color: var(--muted);
    font-size: 18px;
    line-height: 1;
    cursor: pointer;
    padding: 0 2px;
  }
  .x:hover {
    color: var(--text);
  }
</style>
