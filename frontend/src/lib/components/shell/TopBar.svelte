<script lang="ts">
  // Instance-level top bar (README section 4): logo, project switcher, command palette, spacer, mode badge,
  // user menu. The command palette (Ctrl K) jumps within the current project; cross-type jumping (trace/session
  // ids) is a follow-up, so today it routes a query into the issue list.
  import { router, link } from "../../router.svelte";
  import ProjectSwitcher from "./ProjectSwitcher.svelte";
  import ModeBadge from "./ModeBadge.svelte";
  import UserMenu from "./UserMenu.svelte";
  import Icon from "../ui/Icon.svelte";

  interface Props {
    currentProjectId?: string;
    onsignout: () => void;
  }
  let { currentProjectId, onsignout }: Props = $props();

  let query = $state("");
  let input = $state<HTMLInputElement | null>(null);

  function onKey(e: KeyboardEvent) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      input?.focus();
    }
  }

  function submit(e: Event) {
    e.preventDefault();
    const q = query.trim();
    if (!q || !currentProjectId) return;
    router.navigate(router.projectHref(currentProjectId, `/issues?q=${encodeURIComponent(q)}`));
    query = "";
    input?.blur();
  }
</script>

<svelte:window onkeydown={onKey} />

<header class="top">
  <a href="/" use:link class="logo mono">tamp<span class="muted">/</span>observer</a>

  <ProjectSwitcher {currentProjectId} />

  <form class="field palette" onsubmit={submit}>
    <Icon name="search" size={14} />
    <input
      bind:this={input}
      bind:value={query}
      placeholder="Jump to issue, trace, session…"
      aria-label="Command palette"
    />
    <span class="kbd">Ctrl K</span>
  </form>

  <div class="spacer"></div>

  <ModeBadge />
  <UserMenu {onsignout} />
</header>

<style>
  .top {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--gap-3);
    padding: var(--gap-2) var(--gap-4);
    background: var(--bg-chrome);
    border-bottom: 1px solid var(--border);
  }
  .logo {
    font-weight: 700;
    color: var(--text);
    font-size: 13px;
  }
  .palette {
    flex: 1;
    max-width: 420px;
    min-width: 180px;
  }
  .spacer {
    flex: 1;
  }
</style>
