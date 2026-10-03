<script lang="ts">
  import Icon from "../ui/Icon.svelte";

  // README section 6, panel C pattern: a control forbidden by enforcement mode is rendered, disabled, with a
  // lock icon and a violet reason nearby. Never silently hidden.
  interface Props {
    reason: string;
  }
  let { reason }: Props = $props();
</script>

<span class="locked">
  <Icon name="lock" size={12} />
  <span>{reason}</span>
</span>

<style>
  .locked {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-1);
    color: var(--enf-fg);
    font-size: var(--fs-label);
  }
</style>
