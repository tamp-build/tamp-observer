<script lang="ts">
  import { onMount, onDestroy } from "svelte";
  import rrwebPlayer from "rrweb-player";
  import "rrweb-player/dist/style.css";

  // The rrweb replay player wrapped as a Svelte component (ADR 0014): framework-agnostic JS hosted natively,
  // no interop marshaling. It reconstructs the session in a sandboxed iframe from the event log.
  let { events }: { events: unknown[] } = $props();

  let container = $state<HTMLDivElement>();
  let player: { $destroy?: () => void } | null = null;

  onMount(() => {
    // rrweb needs at least the full snapshot plus one event to play.
    if (container && events && events.length >= 2) {
      player = new rrwebPlayer({
        target: container,
        props: { events, autoPlay: false, showController: true },
      }) as unknown as { $destroy?: () => void };
    }
  });

  onDestroy(() => player?.$destroy?.());
</script>

{#if events && events.length >= 2}
  <div bind:this={container}></div>
{:else}
  <p class="empty">Not enough events to replay this session yet.</p>
{/if}

<style>
  .empty {
    color: #9aa4b2;
    font-size: 0.9rem;
  }
</style>
