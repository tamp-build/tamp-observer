<script lang="ts">
  // Mobile triage bottom tab bar (README 7.7 / TOBS-31). Shown only at phone width; the primary triage surfaces
  // one tap away with 44px+ targets. Desktop keeps the sidebar.
  import { router, link } from "../../router.svelte";
  import Icon, { type IconName } from "../ui/Icon.svelte";

  interface Props {
    projectId: string;
  }
  let { projectId }: Props = $props();

  const base = $derived(router.projectHref(projectId));
  const items = $derived<{ name: string; label: string; icon: IconName; href: string }[]>([
    { name: "overview", label: "Overview", icon: "activity", href: base },
    { name: "issues", label: "Issues", icon: "alert", href: `${base}/issues` },
    { name: "alerts", label: "Alerts", icon: "bell", href: `${base}/alerts` },
    { name: "replay", label: "Replay", icon: "play", href: `${base}/replay` },
  ]);
</script>

<nav class="tabbar" aria-label="Sections">
  {#each items as item (item.name)}
    <a href={item.href} use:link data-keep-filters="true" class:on={router.route.name === item.name}>
      <Icon name={item.icon} size={18} />
      <span>{item.label}</span>
    </a>
  {/each}
</nav>

<style>
  .tabbar {
    position: fixed;
    bottom: 0;
    left: 0;
    right: 0;
    z-index: 50;
    display: none;
    background: var(--bg-chrome);
    border-top: 1px solid var(--border);
    padding-bottom: env(safe-area-inset-bottom, 0);
  }
  .tabbar a {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 3px;
    min-height: 52px;
    color: var(--text-nav);
    font-size: 11px;
  }
  .tabbar a.on {
    color: var(--accent);
  }
  @media (max-width: 640px) {
    .tabbar {
      display: flex;
    }
  }
</style>
