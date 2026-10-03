<script lang="ts">
  // Enforcement admin (README 7.6 / section 6). Reads the instance posture (GET /api/enforcement). The
  // three-step dial and the refused-loosenings list; mode changes are a follow-up (write endpoint is a gap).
  import { instance } from "../../stores/instance.svelte";
  import Panel from "../../components/ui/Panel.svelte";
  import Icon from "../../components/ui/Icon.svelte";

  const MODES = ["advisory", "enforcing", "locked"];
  const mode = $derived(instance.enforcement?.mode ?? "advisory");
  const locked = $derived(instance.enforcement?.locked ?? false);

  const refused = [
    "Identity capture in production",
    "Sending sessions without consent",
    "Masking below strict",
    "Weakening RBAC",
    "Outbound / reachback channels",
    "Capture policy below the floor",
    "Disabling audit",
  ];
</script>

<header class="surface-head"><h1>Enforcement</h1></header>

<Panel label="Mode">
  <div class="dial">
    {#each MODES as m, i (m)}
      <div class="step" class:on={m === mode}>
        <span class="dot" class:on={m === mode}></span>
        <span class="lbl">{m}</span>
      </div>
      {#if i < MODES.length - 1}<span class="line"></span>{/if}
    {/each}
  </div>
  {#if locked}
    <p class="enf-note"><Icon name="lock" size={13} /> The posture is sealed. Loosenings cannot be re-enabled from the UI.</p>
  {/if}
</Panel>

<Panel label="Refused under enforcing / locked">
  <ul class="refused">
    {#each refused as r (r)}
      <li><Icon name="lock" size={12} />{r}</li>
    {/each}
  </ul>
</Panel>

<style>
  .surface-head h1 {
    margin: 0;
  }
  .dial {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
  }
  .step {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    color: var(--muted);
    text-transform: capitalize;
  }
  .step.on {
    color: var(--enf-fg);
    font-weight: 600;
  }
  .dot {
    width: 12px;
    height: 12px;
    border-radius: 50%;
    border: 2px solid var(--border-ctl);
  }
  .dot.on {
    background: var(--enf-fg);
    border-color: var(--enf-fg);
  }
  .line {
    flex: 1;
    height: 2px;
    max-width: 48px;
    background: var(--border-ctl);
  }
  .enf-note {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    color: var(--enf-fg);
    margin: var(--gap-3) 0 0;
  }
  .refused {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: var(--gap-2);
  }
  .refused li {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    color: var(--text-2);
  }
</style>
