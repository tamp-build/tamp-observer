<script lang="ts">
  // Catches errors thrown while rendering its children (Svelte 5 <svelte:boundary>), reports them to the error
  // store, and shows a real error state with a reset instead of a frozen/blank view.
  import type { Snippet } from "svelte";
  import { errors } from "../../stores/errors.svelte";
  import ErrorState from "./ErrorState.svelte";

  interface Props {
    children: Snippet;
  }
  let { children }: Props = $props();
</script>

<svelte:boundary onerror={(error) => errors.report("render", error)}>
  {@render children()}

  {#snippet failed(error, reset)}
    <ErrorState message={`This view failed to render: ${error instanceof Error ? error.message : String(error)}`} onretry={reset} />
  {/snippet}
</svelte:boundary>
