import { errors, type AppError } from "./stores/errors.svelte";
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

interface ClientEventPayload {
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

function toPayload(e: AppError): ClientEventPayload {
  return {
    kind: e.kind,
    level: e.kind === "console" ? "error" : "error",
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
