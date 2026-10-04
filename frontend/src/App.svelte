<script lang="ts">
  import "./lib/styles/tokens.css";
  import "./lib/styles/app.css";

  import { onMount } from "svelte";
  import { setToken, setUnauthorizedHandler } from "./lib/api/client";
  import { initAuth, resolveUser, login, logout, isBundledDevAuth, takeReturnTo } from "./lib/auth";
  import { startRecording, type Recording } from "./lib/replay/recorder";
  import { session } from "./lib/stores/session.svelte";
  import { instance } from "./lib/stores/instance.svelte";
  import { router } from "./lib/router.svelte";
  import { loadConfig } from "./lib/config";

  import AppShell from "./lib/components/shell/AppShell.svelte";
  import NotAdmitted from "./lib/components/access/NotAdmitted.svelte";
  import EmptyState from "./lib/components/ui/EmptyState.svelte";
  import ErrorBoundary from "./lib/components/ui/ErrorBoundary.svelte";
  import ErrorToast from "./lib/components/ui/ErrorToast.svelte";

  import Overview from "./lib/routes/Overview.svelte";
  import Issues from "./lib/routes/Issues.svelte";
  import IssueDetail from "./lib/routes/IssueDetail.svelte";
  import Trace from "./lib/routes/Trace.svelte";
  import Logs from "./lib/routes/Logs.svelte";
  import Replay from "./lib/routes/Replay.svelte";
  import Alerts from "./lib/routes/Alerts.svelte";
  import Users from "./lib/routes/admin/Users.svelte";
  import Channels from "./lib/routes/admin/Channels.svelte";
  import Enforcement from "./lib/routes/admin/Enforcement.svelte";
  import StorageHealth from "./lib/routes/admin/StorageHealth.svelte";

  let booting = $state(true);
  let recording: Recording | null = null;
  // Guards against redirecting to the IdP more than once if 401s arrive in a burst (TOBS-40).
  let reauthing = false;

  const route = $derived(router.route);
  const projectId = $derived(route.params.projectId ?? instance.projects[0]?.id);

  // Mid-session the bearer can expire; the API then 401s. Bounce the user back through the IdP (preserving the
  // current route) instead of leaving a silently broken page. A 401 while we still hold a non-expired token is a
  // server/config problem, not an expiry, so we do not loop the user through login for it.
  async function reauth(): Promise<void> {
    if (reauthing) return;
    const u = session.user;
    if (u && u.expired === false) return;
    reauthing = true;
    setToken(null);
    session.reset();
    await login();
  }

  onMount(async () => {
    await initAuth();
    setUnauthorizedHandler(() => void reauth());
    session.devAuth = isBundledDevAuth();
    const user = await resolveUser();

    if (!user) {
      // No landing page: go straight to the IdP (README section 5), guarded against a cancelled-login loop.
      if (!sessionStorage.getItem("tobs_login_bounced")) {
        sessionStorage.setItem("tobs_login_bounced", "1");
        await login();
        return;
      }
      booting = false;
      return;
    }

    sessionStorage.removeItem("tobs_login_bounced");
    session.user = user;
    setToken(user.id_token ?? null);

    await session.loadMe();
    if (!session.notAdmitted) {
      await instance.load();
      const cfg = await loadConfig();
      recording = startRecording(cfg.clientProjectKey || "spa");
      // Land the user back where they were before the IdP round-trip, if anywhere (TOBS-40).
      const returnTo = takeReturnTo();
      if (returnTo && returnTo !== "/" && returnTo !== route.path) {
        router.navigate(returnTo, { replace: true });
      }
    }
    booting = false;
  });

  async function signOut() {
    await logout();
    setToken(null);
    session.reset();
    router.navigate("/", { replace: true });
    location.reload();
  }

  // Home with projects resolved: send to the first project's issues.
  $effect(() => {
    if (!booting && session.me && route.name === "home" && projectId) {
      router.navigate(router.projectHref(projectId, "/issues"), { replace: true });
    }
  });
</script>

{#if booting}
  <div class="boot"><p class="muted">Loading…</p></div>
{:else if !session.user}
  <div class="boot">
    <div class="panel signin">
      <h1>tamp<span class="muted">/</span>observer</h1>
      <p class="muted">Sign-in was cancelled.</p>
      <button class="btn pri" onclick={() => login()}>Sign in</button>
    </div>
  </div>
{:else if session.notAdmitted}
  <NotAdmitted onswitch={signOut} />
{:else}
  <AppShell {projectId} onsignout={signOut}>
    {#key route.path}
    <ErrorBoundary>
    {#if !projectId && route.name !== "not-admitted"}
      <EmptyState message="You do not have access to any projects yet. Ask an admin to grant access." />
    {:else if route.name === "overview"}
      <Overview {projectId} />
    {:else if route.name === "issues"}
      <Issues {projectId} />
    {:else if route.name === "issue-detail"}
      <IssueDetail {projectId} issueId={route.params.issueId} />
    {:else if route.name === "trace"}
      <Trace {projectId} traceId={route.params.traceId} />
    {:else if route.name === "logs"}
      <Logs {projectId} />
    {:else if route.name === "replay"}
      <Replay {projectId} />
    {:else if route.name === "replay-session"}
      <Replay {projectId} sessionId={route.params.sessionId} />
    {:else if route.name === "alerts"}
      <Alerts {projectId} />
    {:else if route.name === "admin-users"}
      <Users />
    {:else if route.name === "admin-channels"}
      <Channels />
    {:else if route.name === "admin-enforcement"}
      <Enforcement />
    {:else if route.name === "admin-storage"}
      <StorageHealth />
    {:else if route.name === "home"}
      <EmptyState message="Loading project…" />
    {:else}
      <EmptyState message="Page not found." />
    {/if}
    </ErrorBoundary>
    {/key}
  </AppShell>
{/if}

<ErrorToast />

<style>
  .boot {
    min-height: 100vh;
    display: grid;
    place-items: center;
    background: var(--bg-sunken);
  }
  .signin {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: var(--gap-3);
    padding: var(--gap-5);
  }
</style>
