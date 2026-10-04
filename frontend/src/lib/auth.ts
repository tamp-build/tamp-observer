import { UserManager, WebStorageStateStore, type User } from "oidc-client-ts";

// Browser OIDC login (authorization code + PKCE) against the configured provider (ADR 0013). The OIDC settings
// come from the backend at runtime (GET /config.json), not baked at build time, so one image works across
// deployments. A build-time VITE_OIDC_* still wins if set (useful for `vite dev` against a local backend).

interface RuntimeConfig {
  oidcAuthority: string;
  oidcClientId: string;
}

let manager: UserManager | null = null;
let devAuth = false;

/// Fetch runtime OIDC config and build the UserManager. Call once before resolveUser/login.
export async function initAuth(): Promise<void> {
  if (manager) return;

  let authority = import.meta.env.VITE_OIDC_AUTHORITY ?? "";
  let clientId = import.meta.env.VITE_OIDC_CLIENT_ID ?? "";
  if (!authority) {
    try {
      const res = await fetch("/config.json", { cache: "no-store" });
      if (res.ok) {
        const cfg = (await res.json()) as RuntimeConfig;
        authority = cfg.oidcAuthority || authority;
        clientId = clientId || cfg.oidcClientId;
      }
    } catch {
      // Fall through to the dev default below (e.g. vite dev with no backend yet).
    }
  }
  authority = authority || "http://id.localhost:5556/dex";
  clientId = clientId || "tamp-observer";
  devAuth = authority.includes("id.localhost");

  manager = new UserManager({
    authority,
    client_id: clientId,
    redirect_uri: window.location.origin + "/",
    post_logout_redirect_uri: window.location.origin + "/",
    response_type: "code",
    scope: "openid profile email",
    userStore: new WebStorageStateStore({ store: window.localStorage }),
    automaticSilentRenew: false,
  });
}

/// True when configured against the bundled dev issuer, so the UI can label itself as demo auth.
export function isBundledDevAuth(): boolean {
  return devAuth;
}

function mgr(): UserManager {
  if (!manager) throw new Error("initAuth() must be called before using auth");
  return manager;
}

/// Resolve the current user: completes the redirect if returning from the IdP (code in the URL), otherwise
/// returns any stored session. Cleans the auth params from the URL after a successful callback. A stored but
/// expired session resolves to null so the caller treats it as signed-out and bounces to the IdP (TOBS-40):
/// we do not use refresh tokens (no offline_access scope), so a dead token cannot be renewed in place.
export async function resolveUser(): Promise<User | null> {
  const params = new URLSearchParams(window.location.search);
  if (params.has("code") && params.has("state")) {
    try {
      const user = await mgr().signinRedirectCallback();
      window.history.replaceState({}, document.title, window.location.pathname);
      return user && !user.expired ? user : null;
    } catch {
      window.history.replaceState({}, document.title, window.location.pathname);
      return null;
    }
  }
  const user = await mgr().getUser();
  return user && !user.expired ? user : null;
}

// Where to send the user after a successful login. Survives the IdP round-trip in sessionStorage (same tab),
// since our redirect_uri is always the origin root and so cannot itself carry the intended route.
const RETURN_KEY = "tobs_return_to";

/// Begin the IdP redirect. Stashes where to come back to (the current route by default) so re-auth lands the
/// user where they were, not on the home page (TOBS-40).
export function login(returnTo?: string): Promise<void> {
  try {
    const target = returnTo ?? window.location.pathname + window.location.search + window.location.hash;
    // Never stash a callback URL (code/state) as the return target.
    if (!target.includes("code=")) sessionStorage.setItem(RETURN_KEY, target);
  } catch {
    // sessionStorage can throw in locked-down contexts; losing the return target is non-fatal.
  }
  return mgr().signinRedirect();
}

/// Read and clear the stashed post-login return route, if any.
export function takeReturnTo(): string | null {
  try {
    const v = sessionStorage.getItem(RETURN_KEY);
    if (v) sessionStorage.removeItem(RETURN_KEY);
    return v;
  } catch {
    return null;
  }
}

/// Local logout: clear the stored session (Dex has no RP-initiated logout). Next login re-prompts.
export async function logout(): Promise<void> {
  await mgr().removeUser();
}
