<script lang="ts">
  // Shown only when signed in through the bundled dev IdP (README section 4). Never in a real deployment;
  // decided from session.devAuth, which is set from the runtime OIDC authority.
  import { session } from "../../stores/session.svelte";
</script>

{#if session.devAuth}
  <div class="demo">
    <strong>Development / demo mode.</strong>
    Signed in through the bundled Dex dev identity provider. Not for production.
  </div>
{/if}

<style>
  .demo {
    padding: 6px var(--gap-4);
    background: var(--demo-bg);
    color: var(--demo-fg);
    border-bottom: 1px solid var(--demo-border);
    font-size: var(--fs-label);
    text-align: center;
  }
</style>
