<script lang="ts">
  import { session } from "../../stores/session.svelte";
  import Icon from "../ui/Icon.svelte";

  interface Props {
    onsignout: () => void;
  }
  let { onsignout }: Props = $props();

  let open = $state(false);

  const initials = $derived(
    (session.email || "?")
      .replace(/@.*/, "")
      .split(/[.\-_]/)
      .map((p) => p[0]?.toUpperCase() ?? "")
      .join("")
      .slice(0, 2) || "?",
  );
</script>

<div class="wrap">
  <button class="trigger" onclick={() => (open = !open)}>
    <span class="av">{initials}</span>
    <Icon name="chevron-down" size={14} />
  </button>
  {#if open}
    <div class="panel pop">
      <div class="who">
        <span class="mono email">{session.email}</span>
        {#if session.role}<span class="tag">{session.role}</span>{/if}
      </div>
      <button class="btn" onclick={onsignout}><Icon name="logout" size={14} />Sign out</button>
    </div>
  {/if}
</div>

<style>
  .wrap {
    position: relative;
  }
  .trigger {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-1);
    background: none;
    border: 0;
    color: var(--text-nav);
    cursor: pointer;
    padding: 0;
  }
  .pop {
    position: absolute;
    right: 0;
    top: calc(100% + 6px);
    min-width: 220px;
    padding: var(--gap-3);
    display: flex;
    flex-direction: column;
    gap: var(--gap-3);
    z-index: 20;
  }
  .who {
    display: flex;
    flex-direction: column;
    gap: var(--gap-2);
  }
  .email {
    color: var(--text);
    word-break: break-all;
  }
</style>
