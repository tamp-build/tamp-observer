<script lang="ts">
  // Users & roles admin (README section 9). GET /api/users + POST /api/users/allow, gated by ManageUsers. This
  // is the admission list (ADR 0013): no self-service accounts, so adding an email here is how anyone gets in.
  import { api } from "../../api/client";
  import type { components } from "../../api/schema";
  import LoadingState from "../../components/ui/LoadingState.svelte";
  import EmptyState from "../../components/ui/EmptyState.svelte";
  import ErrorState from "../../components/ui/ErrorState.svelte";
  import Panel from "../../components/ui/Panel.svelte";
  import { timeAgo } from "../../format";
  import { guard, timeout } from "../../net";

  type User = components["schemas"]["UserView"];

  let users = $state<User[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  let email = $state("");
  let role = $state("Viewer");
  let adding = $state(false);
  let addError = $state<string | null>(null);

  async function load() {
    loading = true;
    error = null;
    error = await guard("load users", async () => {
      const { data, response } = await api.GET("/api/users", { ...timeout() });
      if (data) users = data;
      return response;
    });
    loading = false;
  }

  async function add(e: Event) {
    e.preventDefault();
    if (!email.trim()) return;
    adding = true;
    addError = null;
    addError = await guard("add user", async () => {
      const { response } = await api.POST("/api/users/allow", { body: { email: email.trim(), role }, ...timeout() });
      return response;
    });
    adding = false;
    if (!addError) {
      email = "";
      await load();
    }
  }

  $effect(() => {
    load();
  });
</script>

<header class="surface-head"><h1>Users & roles</h1></header>

<Panel label="Admit an account">
  <form class="add" onsubmit={add}>
    <div class="field grow"><input bind:value={email} type="email" placeholder="email@org" aria-label="Email" /></div>
    <div class="field role">
      <select bind:value={role} aria-label="Role">
        <option>Viewer</option>
        <option>Editor</option>
        <option>Admin</option>
      </select>
    </div>
    <button class="btn pri" disabled={adding || !email.trim()}>Add</button>
  </form>
  {#if addError}<p class="err">Could not add ({addError}).</p>{/if}
</Panel>

<section class="panel">
  {#if loading}
    <LoadingState rows={4} />
  {:else if error}
    <ErrorState message={`Could not load users (${error}).`} onretry={load} />
  {:else if users.length === 0}
    <EmptyState message="No admitted accounts yet." />
  {:else}
    <div class="tr th"><span>Email</span><span>Role</span><span>Added</span></div>
    {#each users as u (u.email)}
      <div class="tr">
        <span class="mono">{u.email}</span>
        <span class="tag">{u.role}</span>
        <span class="muted">{timeAgo(u.createdAtUtc)}</span>
      </div>
    {/each}
  {/if}
</section>

<style>
  .surface-head h1 {
    margin: 0;
  }
  .add {
    display: flex;
    gap: var(--gap-2);
    flex-wrap: wrap;
  }
  .grow {
    flex: 1;
    min-width: 200px;
  }
  .role {
    width: 120px;
  }
  .tr {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 100px 120px;
    gap: var(--gap-3);
    align-items: center;
    padding: 8px var(--gap-4);
    border-top: 1px solid var(--divider);
  }
  .th {
    font-size: var(--fs-label);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
    font-weight: 600;
    border-top: 0;
  }
  .err {
    color: var(--err);
    margin: var(--gap-2) 0 0;
  }
</style>
