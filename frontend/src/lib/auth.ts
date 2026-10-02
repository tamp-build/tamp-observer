import { UserManager, WebStorageStateStore, type User } from "oidc-client-ts";

// Browser OIDC login (authorization code + PKCE) against the configured provider (ADR 0013). On the floor this
// is the bundled Dex (see docs/deployment.md); point VITE_OIDC_AUTHORITY elsewhere to use your own IdP. The
// defaults match the floor docker-compose so the demo works with no configuration.
const authority = import.meta.env.VITE_OIDC_AUTHORITY ?? "http://id.localhost:5556/dex";
const clientId = import.meta.env.VITE_OIDC_CLIENT_ID ?? "tamp-observer";

export const userManager = new UserManager({
  authority,
  client_id: clientId,
  redirect_uri: window.location.origin + "/",
  post_logout_redirect_uri: window.location.origin + "/",
  response_type: "code",
  scope: "openid profile email",
  userStore: new WebStorageStateStore({ store: window.localStorage }),
  automaticSilentRenew: false,
});

/// True when the SPA is configured against the bundled dev issuer, so the UI can label itself as demo auth.
export const isBundledDevAuth = authority.includes("id.localhost");

/// Resolve the current user: completes the redirect if we are returning from the IdP (code in the URL),
/// otherwise returns any stored session. Cleans the auth params out of the URL after a successful callback.
export async function resolveUser(): Promise<User | null> {
  const params = new URLSearchParams(window.location.search);
  if (params.has("code") && params.has("state")) {
    try {
      const user = await userManager.signinRedirectCallback();
      window.history.replaceState({}, document.title, window.location.pathname);
      return user;
    } catch {
      window.history.replaceState({}, document.title, window.location.pathname);
      return null;
    }
  }
  return userManager.getUser();
}

export function login(): Promise<void> {
  return userManager.signinRedirect();
}

/// Local logout: clear the stored session. Dex does not implement RP-initiated logout, so there is no IdP
/// round trip here; the next login re-prompts.
export async function logout(): Promise<void> {
  await userManager.removeUser();
}
