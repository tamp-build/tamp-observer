<script lang="ts" module>
  export type IssueStatus = "Unresolved" | "Regressed" | "Resolved" | "Muted";
</script>

<script lang="ts">
  import Icon from "./Icon.svelte";

  interface Props {
    status: IssueStatus;
    /** The emphasized "Regressed in 1.8.2" form (README 7.2). */
    version?: string | null;
  }
  let { status, version = null }: Props = $props();

  const cls: Record<IssueStatus, string> = {
    Unresolved: "unres",
    Regressed: "regr",
    Resolved: "res",
    Muted: "muted-pill",
  };
</script>

<span class="pill {cls[status]}">
  {#if status === "Regressed"}
    <Icon name="alert" size={12} />
    {version ? `Regressed in ${version}` : "Regressed"}
  {:else if status === "Resolved"}
    <Icon name="check" size={12} />Resolved
  {:else}
    {status}
  {/if}
</span>
