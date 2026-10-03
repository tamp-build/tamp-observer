<script lang="ts">
  import Icon from "../ui/Icon.svelte";

  // README section 5, panel B: the user reached a surface by link that they can see in some scope but not this
  // one. Explain what is needed and what they do have, rather than a blank 403.
  interface Props {
    need: string;
    have?: string;
  }
  let { need, have }: Props = $props();
</script>

<div class="panel denied">
  <div class="lock"><Icon name="lock" size={18} /></div>
  <div>
    <h2 class="h">Not available in this scope</h2>
    <p>This view needs <strong>{need}</strong> here.</p>
    {#if have}<p class="muted">You currently have: {have}.</p>{/if}
  </div>
</div>

<style>
  .denied {
    display: flex;
    gap: var(--gap-3);
    align-items: flex-start;
    padding: var(--gap-4);
  }
  .lock {
    color: var(--enf-fg);
  }
  p {
    margin: var(--gap-1) 0 0;
  }
</style>
