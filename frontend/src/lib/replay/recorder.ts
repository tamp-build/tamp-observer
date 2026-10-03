import { record } from "rrweb";

// Client-side session recording (ADR 0010): rrweb captures the DOM as a structured event log, batched into
// bursty chunks shipped to the session front door over the session's life (ADR 0010 section 2). Masking is
// client-side at record time (section 5); this uses default masking (mask all inputs). Consent and
// smart-capture (ADR 0011) are a separate concern and intentionally not here yet.

const ingestUrl = (import.meta.env.VITE_API_BASE ?? "") + "/ingest/replay";
const FLUSH_MS = 5000;

export interface Recording {
  sessionId: string;
  stop: () => void;
}

// The active session id, shared so the client error sink can stamp tamp.session.id on captured errors and tie
// them to this replay (ADR 0010 section 4).
let activeSessionId: string | null = null;
export function currentSessionId(): string | null {
  return activeSessionId;
}

/// Start recording the current page as one session. The project key comes from runtime config so one build
/// reports to the right project. Returns the session id (the correlation key) and a stop handle.
export function startRecording(projectKey: string): Recording {
  const sessionId = crypto.randomUUID();
  activeSessionId = sessionId;
  let buffer: unknown[] = [];

  async function flush(useBeacon = false): Promise<void> {
    if (buffer.length === 0) return;
    const events = buffer;
    buffer = [];
    const body = JSON.stringify({
      sessionId,
      startUrl: location.href,
      userAgent: navigator.userAgent,
      events,
    });
    try {
      await fetch(ingestUrl, {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Tamp-Project-Key": projectKey },
        body,
        keepalive: useBeacon,
      });
    } catch {
      // Network blip: requeue so the next flush retries rather than dropping the chunk.
      buffer = events.concat(buffer);
    }
  }

  const stopRecord = record({
    emit(event) {
      buffer.push(event);
    },
    maskAllInputs: true,
  });

  const timer = window.setInterval(() => void flush(), FLUSH_MS);
  const onUnload = () => void flush(true);
  window.addEventListener("beforeunload", onUnload);

  return {
    sessionId,
    stop() {
      window.clearInterval(timer);
      window.removeEventListener("beforeunload", onUnload);
      stopRecord?.();
      void flush(true);
      activeSessionId = null;
    },
  };
}
