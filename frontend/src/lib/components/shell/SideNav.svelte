<script lang="ts">
  // Project-level sidebar (README section 4). Role-aware: items the user cannot reach anywhere are hidden; the
  // Admin group renders only with ManageUsers/AdministerInstance. Active item is derived from the router.
  import { router, link } from "../../router.svelte";
  import { session, type Capability } from "../../stores/session.svelte";
  import Icon, { type IconName } from "../ui/Icon.svelte";

  interface Props {
    projectId: string;
    issueCount?: number | null;
    alertCount?: number | null;
  }
  let { projectId, issueCount = null, alertCount = null }: Props = $props();

  interface NavItem {
    name: string;
    label: string;
    icon: IconName;
    href: string;
    cap?: Capability;
    built: boolean;
    count?: number | null;
    alert?: boolean;
  }

  const base = $derived(router.projectHref(projectId));

  const items = $derived<NavItem[]>([
    { name: "overview", label: "Overview", icon: "activity", href: base, built: true },
    { name: "issues", label: "Issues", icon: "alert", href: `${base}/issues`, cap: "ViewErrors", built: true, count: issueCount },
    { name: "trace", label: "Traces", icon: "layers", href: `${base}/traces`, cap: "ViewTraces", built: false },
    { name: "logs", label: "Logs", icon: "list", href: `${base}/logs`, cap: "ViewLogs", built: true },
    { name: "replay", label: "Session replay", icon: "play", href: `${base}/replay`, cap: "ViewReplay", built: true },
    { name: "alerts", label: "Alerts", icon: "bell", href: `${base}/alerts`, cap: "ViewErrors", built: true, count: alertCount, alert: true },
  ]);

  const adminItems: NavItem[] = [
    { name: "admin-users", label: "Users & roles", icon: "users", href: "/admin/users", cap: "ManageUsers", built: true },
    { name: "admin-channels", label: "Notification channels", icon: "bell", href: "/admin/channels", cap: "AdministerInstance", built: true },
    { name: "admin-enforcement", label: "Enforcement", icon: "shield", href: "/admin/enforcement", cap: "AdministerInstance", built: true },
    { name: "admin-storage", label: "Storage & health", icon: "database", href: "/admin/storage", cap: "AdministerInstance", built: true },
  ];

  function visible(item: NavItem): boolean {
    return !item.cap || session.can(item.cap);
  }
</script>

<nav class="nav">
  {#each items.filter(visible) as item (item.name)}
    {#if item.built}
      <a href={item.href} use:link data-keep-filters="true" class:on={router.route.name === item.name}>
        <span class="lbl"><Icon name={item.icon} size={15} />{item.label}</span>
        {#if item.count != null}<span class="ct" class:alert={item.alert && item.count > 0}>{item.count}</span>{/if}
      </a>
    {:else}
      <span class="item disabled" title="Not built yet">
        <span class="lbl"><Icon name={item.icon} size={15} />{item.label}</span>
        <span class="soon">soon</span>
      </span>
    {/if}
  {/each}

  {#if session.isAdminSurface}
    <h3 class="h">Admin</h3>
    {#each adminItems.filter(visible) as item (item.name)}
      <a href={item.href} use:link class:on={router.route.name === item.name}>
        <span class="lbl"><Icon name={item.icon} size={15} />{item.label}</span>
      </a>
    {/each}
  {/if}
</nav>

<style>
  .lbl {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-2);
  }
  .item.disabled {
    display: flex;
    align-items: center;
    justify-content: space-between;
    min-height: var(--ctl-h);
    padding: 0 10px;
    color: var(--disabled);
    border-radius: var(--r-ctl);
  }
  .soon {
    font-size: 10px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }
</style>
