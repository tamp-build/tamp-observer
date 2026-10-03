// UX sugar for data loads. Capture/timeout are NOT here anymore: the observability adapter (observability.ts)
// patches fetch/XHR globally, so every failure is captured and every request is bounded without any call-site
// code. guard() just maps a response to an error string for an ErrorState, and timeout() is a no-op kept so
// existing call sites compile; a surface that drops both is still fully covered by the adapter.

/** No-op: a global default timeout is applied by the observability adapter. Kept for call-site compatibility. */
export function timeout(): Record<string, never> {
  return {};
}

/**
 * Map a load to an ErrorState message. Returns null on success, or a short message on failure. Does no
 * capturing (the adapter already did); this is purely for the component's own error UI.
 */
export async function guard(
  _context: string,
  run: () => Promise<{ ok: boolean; status: number; statusText: string }>,
): Promise<string | null> {
  const res = await run();
  return res.ok ? null : `${res.status} ${res.statusText}`.trim();
}
