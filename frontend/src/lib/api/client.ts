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
};

export const api = createClient<paths>({
  baseUrl: import.meta.env.VITE_API_BASE ?? "",
});
api.use(authMiddleware);
