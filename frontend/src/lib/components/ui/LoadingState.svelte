<script lang="ts">
  interface Props {
    rows?: number;
  }
  let { rows = 5 }: Props = $props();
</script>

<!-- Skeleton rows matching a list layout (README section 8: every data surface needs a loading state). -->
<div class="skeleton" aria-busy="true" aria-label="Loading">
  {#each Array(rows) as _, i (i)}
    <div class="row" style="opacity: {1 - i * 0.12}"></div>
  {/each}
</div>

<style>
  .skeleton {
    display: flex;
    flex-direction: column;
    gap: var(--gap-2);
    padding: var(--gap-3);
  }
  .row {
    height: 34px;
    border-radius: var(--r-ctl);
    background: linear-gradient(90deg, var(--surface) 0%, var(--surface-hover) 50%, var(--surface) 100%);
    background-size: 200% 100%;
    animation: shimmer 1.3s infinite;
  }
  @keyframes shimmer {
    0% {
      background-position: 200% 0;
    }
    100% {
      background-position: -200% 0;
    }
  }
</style>
