import { errors, type AppError, type ErrorKind } from "./stores/errors.svelte";
import { loadConfig } from "./config";
import { currentSessionId } from "./replay/recorder";

// The backend sink: forwards captured errors to tamp-observer itself via the /ingest/client relay, so the
// platform observes its own frontend and the errors correlate to session replay (tamp.session.id) and the
// server side. Transport lives here, not in the error store. Batches to keep it cheap; flushes on a timer and
// on page hide. Failures are swallowed (never re-captured) so the sink can't feed itself.

const INGEST_URL = (import.meta.env.VITE_API_BASE ?? "") + "/ingest/client";
const FLUSH_MS = 5000;
const MAX_BATCH = 25;
const MAX_QUEUE = 100;

export interface ClientEventPayload {
  kind: string;
  level: string;
  message: string;
  url?: string;
  status?: number;
  stack?: string;
  atUnixMs: number;
}

let projectKey = "";
let queue: ClientEventPayload[] = [];
let timer: number | null = null;

// Severity per captured kind. The server maps level "warn" to severityNumber 13 / WARN and anything
// else to 17 / ERROR, so this is the only thing that decides how a client event shows up in the
// observability data.
//
// `console` is the odd one out and the reason this map exists: only console.error is patched
// (observability.ts patchConsole), so a "console" event is app code choosing to log, not a failure
// the app suffered. Reporting it at the same severity as an unhandled exception or a 5xx makes
// developer log noise indistinguishable from a real fault — exactly what this product exists to
// avoid — and left the server's WARN path unreachable.
//
// Typed as an exhaustive Record<ErrorKind, …> deliberately: adding a kind to ErrorKind without
// choosing a level here is a compile error, so this cannot quietly collapse back into a conditional
// that returns the same value either way (tamp-observer#14).
const LEVEL_BY_KIND: Record<ErrorKind, "warn" | "error"> = {
  console: "warn",
  network: "error",
  timeout: "error",
  server: "error",
  client: "error",
  render: "error",
  script: "error",
  unhandled: "error",
};

/**
 * The wire shape for one captured error. Exported so the kind-to-level mapping can be tested
 * directly: tamp-observer#14 was a bug in exactly this function that no test could have caught,
 * because the frontend had no test runner at all.
 */
export function toPayload(e: AppError): ClientEventPayload {
  return {
    kind: e.kind,
    level: LEVEL_BY_KIND[e.kind],
    message: e.message,
    url: e.url,
    status: e.status,
    stack: e.stack,
    atUnixMs: Date.parse(e.at) || Date.now(),
  };
}

async function flush(useKeepalive = false): Promise<void> {
  if (queue.length === 0 || !projectKey) return;
  const batch = queue.slice(0, MAX_BATCH);
  queue = queue.slice(MAX_BATCH);
  try {
    await fetch(INGEST_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Tamp-Project-Key": projectKey },
      body: JSON.stringify({
        sessionId: currentSessionId(),
        userAgent: navigator.userAgent,
        events: batch,
      }),
      keepalive: useKeepalive,
    });
  } catch {
    // Network blip: requeue (bounded) for the next flush. Never report this failure (would feed the sink).
    queue = batch.concat(queue).slice(0, MAX_QUEUE);
  }
}

function enqueue(e: AppError): void {
  // Never forward our own relay/replay traffic failures: that would be a feedback loop.
  if (e.url && (e.url.includes("/ingest/client") || e.url.includes("/ingest/replay"))) return;
  queue.push(toPayload(e));
  if (queue.length > MAX_QUEUE) queue = queue.slice(-MAX_QUEUE);
  if (queue.length >= MAX_BATCH) void flush();
}

/** Wire the sink once config is known. No-op (sink stays unregistered) when no client project key is set. */
export async function installClientSink(): Promise<void> {
  const cfg = await loadConfig();
  if (!cfg.clientProjectKey) return; // relay disabled for this deployment
  projectKey = cfg.clientProjectKey;

  errors.setSink(enqueue);
  // Drain the backlog: errors captured between installClientObservability() and now (this sink installs
  // asynchronously after fetching /config.json) would otherwise be toasted but never ingested. That window
  // routinely catches the earliest failures (e.g. a resource/script error during initial page load). Forward
  // them oldest-first so the backend sees them too.
  for (const e of [...errors.recent].reverse()) enqueue(e);
  void flush();
  timer = window.setInterval(() => void flush(), FLUSH_MS);
  // Flush on the way out so the last errors are not lost.
  window.addEventListener("pagehide", () => void flush(true));
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") void flush(true);
  });
}

/** Stop the sink (used only in teardown/tests). */
export function stopClientSink(): void {
  if (timer !== null) window.clearInterval(timer);
  errors.setSink(null);
}
