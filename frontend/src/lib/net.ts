import { errors } from "./stores/errors.svelte";

// Shared request helpers. A hanging request should surface as an error, not an endless spinner, so every call
// gets a timeout; and load failures funnel through the error store so nothing fails silently.

export const REQUEST_TIMEOUT_MS = 15000;

/** An options fragment giving an openapi-fetch call a hard timeout (AbortSignal). */
export function timeout(ms: number = REQUEST_TIMEOUT_MS): { signal: AbortSignal } {
  return { signal: AbortSignal.timeout(ms) };
}

/**
 * Run a data load with uniform failure handling: a thrown error (network, abort/timeout) or an HTTP error both
 * end up reported and returned as a message, and the caller's `finally` still runs. Returns null on success,
 * or an error string to show in an ErrorState.
 */
export async function guard(
  context: string,
  run: () => Promise<{ ok: boolean; status: number; statusText: string }>,
): Promise<string | null> {
  try {
    const res = await run();
    if (res.ok) return null;
    const msg = `${res.status} ${res.statusText}`.trim();
    errors.report(context, msg);
    return msg;
  } catch (e) {
    errors.report(context, e);
    return e instanceof Error ? e.message : String(e);
  }
}
