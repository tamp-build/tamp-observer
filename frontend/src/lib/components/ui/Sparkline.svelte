<script lang="ts">
  // A tiny inline-SVG sparkline from a dense bucket array (TOBS-25). Offline-safe, no chart lib. Scales to its
  // own min/max; renders a flat baseline when there is no variation or no data.
  interface Props {
    values: number[];
    color?: string;
    width?: number;
    height?: number;
    fill?: boolean;
  }
  let { values, color = "var(--accent)", width = 120, height = 28, fill = false }: Props = $props();

  const pad = 2;
  const pts = $derived.by(() => {
    const vs = values ?? [];
    if (vs.length < 2) return "";
    const max = Math.max(...vs, 1);
    const min = Math.min(...vs, 0);
    const span = max - min || 1;
    const stepX = (width - pad * 2) / (vs.length - 1);
    return vs
      .map((v, i) => {
        const x = pad + i * stepX;
        const y = pad + (1 - (v - min) / span) * (height - pad * 2);
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(" ");
  });
  const hasData = $derived((values ?? []).some((v) => v > 0));
</script>

<svg {width} {height} viewBox="0 0 {width} {height}" preserveAspectRatio="none" aria-hidden="true">
  {#if hasData && pts}
    {#if fill}
      <polygon points="{pad},{height - pad} {pts} {width - pad},{height - pad}" fill={color} opacity="0.12" />
    {/if}
    <polyline points={pts} fill="none" stroke={color} stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round" vector-effect="non-scaling-stroke" />
  {:else}
    <line x1={pad} y1={height - pad * 2} x2={width - pad} y2={height - pad * 2} stroke="var(--divider)" stroke-width="1.5" />
  {/if}
</svg>
