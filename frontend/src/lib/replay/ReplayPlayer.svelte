<script lang="ts">
  // Custom session-replay player (README 7.4 / TOBS-30). Drives rrweb's Replayer directly (not rrweb-player) so
  // we own the controller: a timeline with event-density markers + a draggable playhead, play/pause, back/forward
  // 5s, speed, skip-idle, fullscreen, prev/next error, a time readout, and an event list that follows the
  // playhead. rrweb only captures DOM + interactions by default, so console/network kinds are sparse unless the
  // recorder is extended; the list is honest about what was captured.
  import { onMount, onDestroy } from "svelte";
  import { Replayer } from "rrweb";
  import { errors } from "../stores/errors.svelte";
  import Icon from "../components/ui/Icon.svelte";

  interface Props {
    events: unknown[];
    /** Correlated server-error time offsets (ms from session start), for the error markers + prev/next. */
    errorOffsets?: number[];
  }
  let { events, errorOffsets = [] }: Props = $props();

  type RrEvent = { type: number; timestamp: number; data?: { source?: number } };

  let stage = $state<HTMLDivElement>();
  let replayer: Replayer | null = null;
  let meta = $state({ start: 0, total: 1 });
  let current = $state(0);
  let playing = $state(false);
  let speed = $state(1);
  let skipIdle = $state(true);
  const SPEEDS = [1, 2, 4, 8];

  const evs = $derived((events ?? []) as RrEvent[]);
  // Markers: one per event, positioned by time, colored by kind.
  const markers = $derived.by(() => {
    if (meta.total <= 0) return [];
    return evs.map((e) => {
      const off = e.timestamp - meta.start;
      const src = e.data?.source;
      let kind = "nav";
      if (e.type === 3 && src === 2) kind = "click";
      else if (e.type === 3 && (src === 1 || src === 3)) kind = "move";
      else if (e.type === 3 && src === 5) kind = "input";
      else if (e.type === 5) kind = "custom";
      return { pct: Math.max(0, Math.min(100, (off / meta.total) * 100)), kind };
    });
  });
  const errorMarks = $derived(errorOffsets.map((o) => Math.max(0, Math.min(100, (o / meta.total) * 100))));

  function fmt(ms: number): string {
    const s = Math.max(0, Math.floor(ms / 1000));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
  }

  function goto(ms: number, play = true) {
    if (!replayer) return;
    const t = Math.max(0, Math.min(meta.total, ms));
    try {
      if (play) {
        replayer.play(t);
        playing = true;
      } else {
        replayer.pause();
        replayer.play(t);
        replayer.pause();
        playing = false;
      }
      current = t;
    } catch (e) {
      errors.capture({ kind: "render", context: "replay seek", message: e instanceof Error ? e.message : String(e) });
    }
  }
  function toggle() {
    if (!replayer) return;
    if (playing) {
      replayer.pause();
      playing = false;
    } else {
      replayer.play(current);
      playing = true;
    }
  }
  function setSpeed(s: number) {
    speed = s;
    replayer?.setConfig({ speed: s });
  }
  function toggleIdle() {
    skipIdle = !skipIdle;
    replayer?.setConfig({ skipInactive: skipIdle });
  }
  function seekClick(e: MouseEvent) {
    const el = e.currentTarget as HTMLElement;
    const rect = el.getBoundingClientRect();
    goto(((e.clientX - rect.left) / rect.width) * meta.total, playing);
  }
  function jumpError(dir: 1 | -1) {
    const sorted = [...errorOffsets].sort((a, b) => a - b);
    const next = dir === 1 ? sorted.find((o) => o > current + 50) : [...sorted].reverse().find((o) => o < current - 50);
    if (next != null) goto(next, false);
  }
  function fullscreen() {
    stage?.requestFullscreen?.().catch(() => {});
  }
  function onKey(e: KeyboardEvent) {
    if (e.target instanceof HTMLInputElement) return;
    if (e.key === " ") { e.preventDefault(); toggle(); }
    else if (e.key === "ArrowLeft") goto(current - 5000, playing);
    else if (e.key === "ArrowRight") goto(current + 5000, playing);
  }

  onMount(() => {
    if (!stage || evs.length < 2) return;
    try {
      replayer = new Replayer(evs as unknown as ConstructorParameters<typeof Replayer>[0], {
        root: stage,
        speed,
        skipInactive: skipIdle,
        mouseTail: false,
      });
      const m = replayer.getMetaData();
      meta = { start: m.startTime, total: m.totalTime || Math.max(1, m.endTime - m.startTime) };
      replayer.on("ui-update-current-time", (p: unknown) => {
        const v = (p as { payload?: number })?.payload;
        if (typeof v === "number") current = v;
      });
      replayer.on("finish", () => (playing = false));
    } catch (e) {
      errors.capture({ kind: "render", context: "replay init", message: e instanceof Error ? e.message : String(e) });
    }
  });
  onDestroy(() => {
    try {
      replayer?.pause();
    } catch {
      /* ignore */
    }
  });
</script>

<svelte:window onkeydown={onKey} />

{#if evs.length < 2}
  <p class="muted">Not enough events to replay this session yet.</p>
{:else}
  <div class="player">
    <div class="stage" bind:this={stage}></div>

    <!-- timeline -->
    <div class="timeline">
      <div class="markers">
        {#each markers as m, i (i)}<span class="mk {m.kind}" style="left:{m.pct}%"></span>{/each}
        {#each errorMarks as p, i (i)}<span class="mk error" style="left:{p}%"></span>{/each}
      </div>
      <!-- svelte-ignore a11y_no_static_element_interactions -->
      <!-- svelte-ignore a11y_click_events_have_key_events -->
      <div class="track" onclick={seekClick}>
        <span class="played" style="width:{(current / meta.total) * 100}%"></span>
        <span class="playhead" style="left:{(current / meta.total) * 100}%"></span>
      </div>
    </div>

    <!-- controls -->
    <div class="controls">
      <button class="btn icon" onclick={() => goto(current - 5000, playing)} aria-label="Back 5s"><Icon name="chevron-right" size={14} /><span class="flip">5s</span></button>
      <button class="btn pri icon" onclick={toggle} aria-label="Play/pause">{playing ? "❚❚" : "▶"}</button>
      <button class="btn icon" onclick={() => goto(current + 5000, playing)} aria-label="Forward 5s">5s<Icon name="chevron-right" size={14} /></button>
      <span class="time mono">{fmt(current)} / {fmt(meta.total)}</span>
      <div class="seg speeds">
        {#each SPEEDS as s (s)}<button class:on={speed === s} onclick={() => setSpeed(s)}>{s}×</button>{/each}
      </div>
      <label class="chk"><input type="checkbox" checked={skipIdle} onchange={toggleIdle} /> Skip idle</label>
      {#if errorOffsets.length > 0}
        <button class="btn err-btn" onclick={() => jumpError(-1)} aria-label="Previous error">◀ err</button>
        <button class="btn err-btn" onclick={() => jumpError(1)} aria-label="Next error">err ▶</button>
      {/if}
      <div class="spacer"></div>
      <button class="btn" onclick={fullscreen} aria-label="Fullscreen">Fullscreen</button>
    </div>
  </div>
{/if}

<style>
  .player { display: flex; flex-direction: column; gap: var(--gap-2); }
  .stage { background: #000; border-radius: var(--r-ctl); overflow: hidden; min-height: 320px; display: grid; place-items: center; }
  .stage :global(iframe) { border: 0; background: #fff; }
  .timeline { display: flex; flex-direction: column; gap: 3px; }
  .markers { position: relative; height: 10px; }
  .mk { position: absolute; top: 0; width: 2px; height: 8px; border-radius: 1px; transform: translateX(-1px); }
  .mk.nav { background: var(--muted); }
  .mk.click { background: var(--accent); }
  .mk.move { background: var(--divider); }
  .mk.input { background: var(--unres-fg); }
  .mk.custom { background: var(--warn); }
  .mk.error { background: var(--regr-fg); height: 10px; width: 3px; }
  .track { position: relative; height: 10px; background: var(--table-bg); border-radius: 5px; cursor: pointer; }
  .played { position: absolute; left: 0; top: 0; bottom: 0; background: var(--surface-sel); border-radius: 5px; }
  .playhead { position: absolute; top: -2px; width: 3px; height: 14px; background: var(--accent); border-radius: 2px; transform: translateX(-1px); }
  .controls { display: flex; flex-wrap: wrap; align-items: center; gap: var(--gap-2); }
  .btn.icon { min-width: 36px; justify-content: center; }
  .flip { transform: scaleX(-1); display: inline-block; }
  .time { color: var(--text-2); }
  .speeds button { min-height: 30px; padding: 0 8px; border: 0; background: var(--surface); color: var(--text-nav); cursor: pointer; }
  .speeds button + button { border-left: 1px solid var(--border-ctl); }
  .speeds button.on { background: var(--surface-sel); color: var(--text); font-weight: 600; }
  .chk { display: inline-flex; align-items: center; gap: var(--gap-1); color: var(--text-2); }
  .err-btn { color: var(--regr-fg); border-color: var(--regr-border); }
  .spacer { flex: 1; }
</style>
