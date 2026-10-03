<script lang="ts">
  // The one shell every signed-in surface shares (README section 4): demo banner, top bar, project sidebar,
  // content. Below ~900px the sidebar collapses into a drawer behind a menu button.
  import type { Snippet } from "svelte";
  import { router } from "../../router.svelte";
  import TopBar from "./TopBar.svelte";
  import SideNav from "./SideNav.svelte";
  import DemoBanner from "./DemoBanner.svelte";
  import Icon from "../ui/Icon.svelte";

  interface Props {
    projectId?: string;
    issueCount?: number | null;
    alertCount?: number | null;
    onsignout: () => void;
    children: Snippet;
  }
  let { projectId, issueCount = null, alertCount = null, onsignout, children }: Props = $props();

  let drawerOpen = $state(false);

  // Close the drawer whenever the route changes.
  $effect(() => {
    void router.route;
    drawerOpen = false;
  });
</script>

<div class="shell">
  <DemoBanner />
  <TopBar currentProjectId={projectId} {onsignout} />

  <div class="body">
    {#if projectId}
      <button class="btn drawer-toggle" onclick={() => (drawerOpen = !drawerOpen)}>
        <Icon name="menu" size={16} />Menu
      </button>
      <aside class="side" class:open={drawerOpen}>
        <SideNav {projectId} {issueCount} {alertCount} />
      </aside>
    {/if}

    <main class="content">
      {@render children()}
    </main>
  </div>
</div>

<style>
  .shell {
    min-height: 100vh;
    display: flex;
    flex-direction: column;
    background: var(--bg);
  }
  .body {
    flex: 1;
    display: grid;
    grid-template-columns: 220px minmax(0, 1fr);
    align-items: start;
  }
  .side {
    position: sticky;
    top: 0;
    align-self: stretch;
  }
  .content {
    min-width: 0;
    padding: var(--gap-4);
    display: flex;
    flex-direction: column;
    gap: var(--gap-4);
  }
  .drawer-toggle {
    display: none;
  }

  @media (max-width: 900px) {
    .body {
      grid-template-columns: 1fr;
    }
    .drawer-toggle {
      display: inline-flex;
      margin: var(--gap-3) var(--gap-4) 0;
    }
    .side {
      position: static;
      display: none;
    }
    .side.open {
      display: block;
    }
  }
</style>
