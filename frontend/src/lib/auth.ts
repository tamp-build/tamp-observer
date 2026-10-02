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
/// returns any stored session. Cleans the auth params from the URL after a successful callback.
export async function resolveUser(): Promise<User | null> {
  const params = new URLSearchParams(window.location.search);
  if (params.has("code") && params.has("state")) {
    try {
      const user = await mgr().signinRedirectCallback();
      window.history.replaceState({}, document.title, window.location.pathname);
      return user;
    } catch {
      window.history.replaceState({}, document.title, window.location.pathname);
      return null;
    }
  }
  return mgr().getUser();
}

export function login(): Promise<void> {
  return mgr().signinRedirect();
}

/// Local logout: clear the stored session (Dex has no RP-initiated logout). Next login re-prompts.
export async function logout(): Promise<void> {
  await mgr().removeUser();
}
