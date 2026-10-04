import createClient, { type Middleware } from "openapi-fetch";
import type { paths } from "./schema";
import { currentSessionId } from "../replay/recorder";

// The typed client the Svelte app talks to the backend through (ADR 0014). Types come from schema.d.ts,
// generated from the backend's own OpenAPI document, so the frontend cannot drift from the API contract
// without a type error at build time.

let bearerToken: string | null = null;

/// Set (or clear) the external-IdP bearer token used on every request (ADR 0013: authN is always external).
export function setToken(token: string | null): void {
  bearerToken = token && token.trim().length > 0 ? token.trim() : null;
}

export function hasToken(): boolean {
  return bearerToken !== null;
}

// Invoked when the API answers 401 (an expired or missing bearer). The app registers a handler that bounces the
// user to the IdP rather than leaving a silently broken view (TOBS-40). Kept as a callback so this module does
// not import the auth/session layer (which imports this client).
let onUnauthorized: (() => void) | null = null;

export function setUnauthorizedHandler(fn: (() => void) | null): void {
  onUnauthorized = fn;
}

const authMiddleware: Middleware = {
  onRequest({ request }) {
    if (bearerToken) {
      request.headers.set("Authorization", `Bearer ${bearerToken}`);
    }
    // Stamp the replay session id so the server span carries it and errors correlate to the session (ADR 0010).
    const sid = currentSessionId();
    if (sid) {
      request.headers.set("X-Tamp-Session-Id", sid);
    }
    return request;
  },
  onResponse({ response }) {
    // A 401 means the bearer is gone or expired; hand off to the registered re-auth handler. 403 is distinct
    // (authenticated but not admitted) and is handled by the session store, not here.
    if (response.status === 401) {
      onUnauthorized?.();
    }
    return response;
  },
};

export const api = createClient<paths>({
  baseUrl: import.meta.env.VITE_API_BASE ?? "",
});
api.use(authMiddleware);
