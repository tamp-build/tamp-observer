<script lang="ts">
  import { session } from "../../stores/session.svelte";
  import Icon from "../ui/Icon.svelte";

  interface Props {
    onswitch?: () => void;
  }
  let { onswitch }: Props = $props();

  let copied = $state(false);

  async function copyEmail() {
    try {
      await navigator.clipboard.writeText(session.email);
      copied = true;
      setTimeout(() => (copied = false), 1500);
    } catch {
      copied = false;
    }
  }
</script>

<!-- README section 5, panel A: signed in at the IdP but the email is not pre-registered, so every /api call
     403s. Never a raw 403; explain it and give a path forward. -->
<div class="wrap">
  <div class="panel card">
    <div class="shield"><Icon name="shield" size={22} /></div>
    <h1>Not on the admission list</h1>
    <p class="muted">
      You are signed in, but this instance only admits pre-registered accounts. Ask an administrator to add the
      email below, then reload.
    </p>
    <div class="email">
      <span class="mono">{session.email}</span>
      <button class="btn" onclick={copyEmail}>
        <Icon name="copy" size={14} />{copied ? "Copied" : "Copy"}
      </button>
    </div>
    <div class="actions">
      <button class="btn pri" onclick={() => location.reload()}>
        <Icon name="refresh" size={14} />Reload
      </button>
      {#if onswitch}
        <button class="btn" onclick={onswitch}>
          <Icon name="logout" size={14} />Use a different account
        </button>
      {/if}
    </div>
  </div>
</div>

<style>
  .wrap {
    min-height: 100vh;
    display: grid;
    place-items: center;
    padding: var(--gap-4);
    background: var(--bg-sunken);
  }
  .card {
    max-width: 460px;
    display: flex;
    flex-direction: column;
    gap: var(--gap-3);
    padding: var(--gap-5);
    text-align: center;
    align-items: center;
  }
  .shield {
    color: var(--enf-fg);
  }
  .email {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    padding: var(--gap-2) var(--gap-3);
    border: 1px solid var(--border-ctl);
    border-radius: var(--r-ctl);
    background: var(--surface);
    width: 100%;
    justify-content: space-between;
  }
  .actions {
    display: flex;
    gap: var(--gap-2);
    flex-wrap: wrap;
    justify-content: center;
  }
</style>
