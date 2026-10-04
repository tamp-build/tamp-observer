<script lang="ts">
  // Notification channels (README 7.5, TOBS-29). GET /api/channels + PUT /api/channels/config, PUT
  // /api/channels/routing, POST /api/channels/{type}/test. AdministerInstance only. A reachback channel (one that
  // reaches an outside host) is shown disabled with the reason under enforcing/locked, never hidden. Secrets
  // (Slack webhook, Telegram bot token) are never sent to the browser: the field shows only whether one is set,
  // and a blank value on save keeps the stored secret.
  import { api } from "../../api/client";
  import type { components } from "../../api/schema";
  import LoadingState from "../../components/ui/LoadingState.svelte";
  import ErrorState from "../../components/ui/ErrorState.svelte";
  import LockedReason from "../../components/access/LockedReason.svelte";
  import { router } from "../../router.svelte";
  import { guard, timeout } from "../../net";
  import { timeAgo } from "../../format";

  type ChannelsView = components["schemas"]["ChannelsView"];
  type ChannelConfigView = components["schemas"]["ChannelConfigView"];
  type ChannelFieldView = components["schemas"]["ChannelFieldView"];
  type ChannelTestView = components["schemas"]["ChannelTestView"];

  let view = $state<ChannelsView | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  // Editable form state. Secret fields start blank and only overwrite the stored value when filled.
  let smtp = $state({ enabled: false, host: "", from: "", to: "" });
  let slack = $state({ enabled: false, webhookUrl: "" });
  let telegram = $state({ enabled: false, botToken: "", chatId: "" });
  let routing = $state<Record<string, Set<string>>>({});

  let savingConfig = $state(false);
  let savingRouting = $state(false);
  let testing = $state<string | null>(null);
  let notice = $state<string | null>(null);

  function field(ch: ChannelConfigView, key: string): ChannelFieldView | undefined {
    return ch.fields.find((f) => f.key === key);
  }
  function channel(type: string): ChannelConfigView | undefined {
    return view?.channels.find((c) => c.type === type);
  }

  function hydrate(v: ChannelsView) {
    view = v;
    const s = v.channels.find((c) => c.type === "smtp");
    const sl = v.channels.find((c) => c.type === "slack");
    const tg = v.channels.find((c) => c.type === "telegram");
    smtp = {
      enabled: s?.enabled ?? false,
      host: field(s!, "host")?.value ?? "",
      from: field(s!, "from")?.value ?? "",
      to: field(s!, "to")?.value ?? "",
    };
    slack = { enabled: sl?.enabled ?? false, webhookUrl: "" };
    telegram = { enabled: tg?.enabled ?? false, botToken: "", chatId: field(tg!, "chatId")?.value ?? "" };
    const map: Record<string, Set<string>> = {};
    for (const row of v.routing) map[row.kind] = new Set(row.channels);
    routing = map;
  }

  async function load() {
    loading = true;
    error = null;
    error = await guard("load channels", async () => {
      const { data, response } = await api.GET("/api/channels", { ...timeout() });
      if (data) hydrate(data);
      return response;
    });
    loading = false;
  }

  async function saveConfig() {
    savingConfig = true;
    notice = null;
    const err = await guard("save channel config", async () => {
      const { data, response } = await api.PUT("/api/channels/config", {
        body: {
          smtp: { enabled: smtp.enabled, host: smtp.host, from: smtp.from, to: smtp.to },
          slack: { enabled: slack.enabled, webhookUrl: slack.webhookUrl || null },
          telegram: { enabled: telegram.enabled, botToken: telegram.botToken || null, chatId: telegram.chatId },
        },
        ...timeout(),
      });
      if (data) hydrate(data);
      return response;
    });
    savingConfig = false;
    notice = err ? `Could not save (${err}).` : "Configuration saved.";
  }

  async function saveRouting() {
    savingRouting = true;
    notice = null;
    const rows = (view?.alertKinds ?? []).map((k) => ({ kind: k.key, channels: [...(routing[k.key] ?? [])] }));
    const err = await guard("save routing", async () => {
      const { data, response } = await api.PUT("/api/channels/routing", { body: { rows }, ...timeout() });
      if (data) hydrate(data);
      return response;
    });
    savingRouting = false;
    notice = err ? `Could not save routing (${err}).` : "Routing saved.";
  }

  async function sendTest(type: string) {
    testing = type;
    const err = await guard("send test", async () => {
      const { data, response } = await api.POST("/api/channels/{type}/test", {
        params: { path: { type } },
        ...timeout(),
      });
      if (data && view) {
        // Splice the fresh test result into the matching channel without a full reload.
        view = { ...view, channels: view.channels.map((c) => (c.type === type ? { ...c, lastTest: data } : c)) };
      }
      return response;
    });
    testing = null;
    if (err) notice = `Test failed to run (${err}).`;
  }

  function enabledFor(type: string): boolean {
    return type === "smtp" ? smtp.enabled : type === "slack" ? slack.enabled : telegram.enabled;
  }
  function setEnabled(type: string, on: boolean) {
    if (type === "smtp") smtp = { ...smtp, enabled: on };
    else if (type === "slack") slack = { ...slack, enabled: on };
    else telegram = { ...telegram, enabled: on };
  }

  function toggleRoute(kind: string, type: string, on: boolean) {
    const set = new Set(routing[kind] ?? []);
    if (on) set.add(type);
    else set.delete(type);
    routing = { ...routing, [kind]: set };
  }

  function testLabel(t: ChannelTestView | null | undefined): string {
    if (!t) return "No test run yet";
    const when = timeAgo(t.atUtc);
    return t.ok ? `Last test delivered ${when} · ${t.ms} ms` : `Last test failed ${when}: ${t.error ?? "unknown error"}`;
  }

  $effect(() => {
    load();
  });
</script>

<header class="surface-head"><h1>Notification channels</h1></header>

{#if loading}
  <LoadingState rows={3} />
{:else if error}
  <ErrorState message={`Could not load channels (${error}).`} onretry={load} />
{:else if view}
  {#if !view.reachbackAllowed}
    <div class="panel posture">
      <LockedReason
        reason={`Mode is ${view.mode}: channels that reach an outside host are refused. Email through an in-enclave relay stays available.`}
      />
      <a
        href="/admin/enforcement"
        onclick={(e) => {
          e.preventDefault();
          router.navigate("/admin/enforcement");
        }}>About enforcement</a
      >
    </div>
  {/if}

  <div class="cards">
    {#each view.channels as ch (ch.type)}
      <section class="panel card" class:off={!ch.allowedUnderMode}>
        <header class="card-head">
          <h2 class="name">{ch.label}</h2>
          {#if ch.allowedUnderMode}
            <span class="pill res">Available</span>
          {:else}
            <span class="pill mask">Unavailable</span>
          {/if}
        </header>

        {#if !ch.allowedUnderMode}
          <p class="muted why">
            Refused under {view.mode} mode.
            {#if ch.outsideHost}Posts to <span class="mono">{ch.outsideHost}</span>, outside this network.{/if}
          </p>
        {/if}

        <label class="toggle">
          <input
            type="checkbox"
            checked={enabledFor(ch.type)}
            disabled={!ch.allowedUnderMode}
            onchange={(e) => setEnabled(ch.type, (e.currentTarget as HTMLInputElement).checked)}
          />
          Enabled
        </label>

        {#if ch.type === "smtp"}
          <label class="fl"><span>Relay host</span>
            <div class="field"><input class="mono" bind:value={smtp.host} placeholder={field(ch, "host")?.placeholder} disabled={!ch.allowedUnderMode} /></div>
          </label>
          <label class="fl"><span>From</span>
            <div class="field"><input class="mono" bind:value={smtp.from} placeholder={field(ch, "from")?.placeholder} disabled={!ch.allowedUnderMode} /></div>
          </label>
          <label class="fl"><span>To</span>
            <div class="field"><input class="mono" bind:value={smtp.to} placeholder={field(ch, "to")?.placeholder} disabled={!ch.allowedUnderMode} /></div>
          </label>
        {:else if ch.type === "slack"}
          <label class="fl"><span>Webhook URL {#if field(ch, "webhookUrl")?.set}<span class="tag">configured</span>{/if}</span>
            <div class="field"><input class="mono" type="password" bind:value={slack.webhookUrl} placeholder={field(ch, "webhookUrl")?.set ? "•••••••• (leave blank to keep)" : field(ch, "webhookUrl")?.placeholder} disabled={!ch.allowedUnderMode} /></div>
          </label>
        {:else if ch.type === "telegram"}
          <label class="fl"><span>Bot token {#if field(ch, "botToken")?.set}<span class="tag">configured</span>{/if}</span>
            <div class="field"><input class="mono" type="password" bind:value={telegram.botToken} placeholder={field(ch, "botToken")?.set ? "•••••••• (leave blank to keep)" : field(ch, "botToken")?.placeholder} disabled={!ch.allowedUnderMode} /></div>
          </label>
          <label class="fl"><span>Chat id</span>
            <div class="field"><input class="mono" bind:value={telegram.chatId} placeholder={field(ch, "chatId")?.placeholder} disabled={!ch.allowedUnderMode} /></div>
          </label>
        {/if}

        <div class="card-foot">
          <button class="btn" disabled={!ch.allowedUnderMode || testing === ch.type} onclick={() => sendTest(ch.type)}>
            {testing === ch.type ? "Sending…" : "Send test"}
          </button>
          {#if ch.allowedUnderMode}
            <span class="muted test" class:bad={ch.lastTest && !ch.lastTest.ok}>{testLabel(ch.lastTest)}</span>
          {:else}
            <span class="muted test">Becomes available in advisory mode</span>
          {/if}
        </div>
      </section>
    {/each}
  </div>

  <div class="save-row">
    <button class="btn pri" disabled={savingConfig} onclick={saveConfig}>{savingConfig ? "Saving…" : "Save configuration"}</button>
    {#if notice}<span class="muted notice">{notice}</span>{/if}
  </div>

  <section class="panel routing">
    <header class="routing-head">
      <h2 class="h">Routing · which alerts go where</h2>
      <span class="muted sub">applies to every project unless a project overrides it</span>
    </header>
    <div class="scroll">
      <div class="matrix">
        <div class="mrow mhead">
          <span>Alert kind</span>
          {#each view.channels as ch (ch.type)}<span class="col">{ch.label}</span>{/each}
        </div>
        {#each view.alertKinds as k (k.key)}
          <div class="mrow">
            <span class="kind">{k.name}{#if k.planned}<span class="tag">planned</span>{/if}</span>
            {#each view.channels as ch (ch.type)}
              <span class="col">
                <input
                  type="checkbox"
                  checked={routing[k.key]?.has(ch.type) ?? false}
                  disabled={!ch.allowedUnderMode}
                  aria-label={`Send ${k.name} by ${ch.label}`}
                  onchange={(e) => toggleRoute(k.key, ch.type, (e.currentTarget as HTMLInputElement).checked)}
                />
              </span>
            {/each}
          </div>
        {/each}
      </div>
    </div>
    <div class="routing-foot">
      <button class="btn pri" disabled={savingRouting} onclick={saveRouting}>{savingRouting ? "Saving…" : "Save routing"}</button>
    </div>
  </section>
{/if}

<style>
  .surface-head h1 {
    margin: 0;
  }
  .posture {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap-3);
    padding: var(--gap-3) var(--gap-4);
    background: var(--enf-wash);
    border-color: var(--enf-border);
    margin-bottom: var(--gap-3);
  }
  .cards {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(320px, 100%), 1fr));
    gap: var(--gap-3);
  }
  .card {
    padding: var(--gap-4);
    display: flex;
    flex-direction: column;
    gap: var(--gap-3);
  }
  .card.off {
    opacity: 0.85;
  }
  .card-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  .name {
    font-size: 15px;
    margin: 0;
  }
  .why {
    margin: 0;
  }
  .toggle {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    font-size: var(--fs-label);
    color: var(--text-2);
  }
  .fl {
    display: flex;
    flex-direction: column;
    gap: var(--gap-1);
    font-size: var(--fs-label);
    color: var(--muted);
  }
  .fl span {
    display: inline-flex;
    align-items: center;
    gap: var(--gap-2);
  }
  .card-foot {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    flex-wrap: wrap;
    margin-top: auto;
    padding-top: var(--gap-1);
  }
  .test {
    font-size: 12px;
  }
  .test.bad {
    color: var(--err);
  }
  .save-row {
    display: flex;
    align-items: center;
    gap: var(--gap-3);
    margin: var(--gap-3) 0;
  }
  .notice {
    font-size: var(--fs-label);
  }
  .routing {
    overflow: hidden;
  }
  .routing-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap-2);
    padding: var(--gap-3) var(--gap-4);
    flex-wrap: wrap;
  }
  .sub {
    font-size: 12px;
  }
  .scroll {
    overflow-x: auto;
  }
  .matrix {
    min-width: 560px;
  }
  .mrow {
    display: grid;
    grid-template-columns: minmax(0, 1fr) repeat(3, 110px);
    align-items: center;
    padding: 8px var(--gap-4);
    border-top: 1px solid var(--divider);
  }
  .mhead {
    font-size: var(--fs-label);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
    font-weight: 600;
  }
  .col {
    text-align: center;
  }
  .kind {
    display: flex;
    align-items: center;
    gap: var(--gap-2);
    flex-wrap: wrap;
  }
  .routing-foot {
    padding: var(--gap-3) var(--gap-4);
    border-top: 1px solid var(--divider);
  }
</style>
