<script lang="ts">
  // Enforcement mode badge, always visible in the top bar (README section 6). Click opens an explainer of what
  // the current mode restricts. Violet = policy; never a status color.
  import { instance } from "../../stores/instance.svelte";
  import { router } from "../../router.svelte";
  import Icon from "../ui/Icon.svelte";

  let open = $state(false);

  const mode = $derived(instance.enforcement?.mode ?? "advisory");
  const locked = $derived(instance.enforcement?.locked ?? false);

  const blurb: Record<string, string> = {
    advisory: "Advisory: policy violations are surfaced but nothing is blocked.",
    enforcing: "Enforcing: loosenings (identity in prod, sub-strict masking, outbound channels, weaker RBAC) are refused.",
    locked: "Locked: the posture is sealed. Loosenings are refused and cannot be re-enabled from the UI.",
  };

  function openEnforcement() {
    open = false;
    router.navigate("/admin/enforcement");
  }
</script>

<div class="wrap">
  <button class="pill enf" onclick={() => (open = !open)} title="Enforcement mode">
    {#if locked}<Icon name="lock" size={12} />{:else}<Icon name="shield" size={12} />{/if}
    {mode}
  </button>
  {#if open}
    <div class="panel pop">
      <p>{blurb[mode] ?? blurb.advisory}</p>
      <button class="btn" onclick={openEnforcement}>Open enforcement settings</button>
    </div>
  {/if}
</div>

<style>
  .wrap {
    position: relative;
  }
  .pop {
    position: absolute;
    right: 0;
    top: calc(100% + 6px);
    width: 280px;
    padding: var(--gap-3);
    display: flex;
    flex-direction: column;
    gap: var(--gap-2);
    z-index: 20;
  }
  .pop p {
    margin: 0;
    color: var(--text-2);
  }
</style>
