import type { User } from "oidc-client-ts";
import { api } from "../api/client";
import type { components } from "../api/schema";

// The signed-in session: the OIDC user plus the backend's view of who they are (GET /api/me: admission + role
// + capabilities, ADR 0013). Capabilities are the single source of truth for what the UI offers; every gated
// control runs through can(). Scope-aware grants are future work, so can() takes an optional scope today and
// ignores it (the backend roles are instance-scoped in this MVP, README section 5).

export type Me = components["schemas"]["MeResponse"];

export type Capability =
  | "ViewErrors"
  | "ViewTraces"
  | "ViewLogs"
  | "ViewReplay"
  | "EditCapturePolicy"
  | "ManageUsers"
  | "AdministerInstance";

export interface Scope {
  project?: string;
  environment?: string;
}

class SessionState {
  user = $state<User | null>(null);
  me = $state<Me | null>(null);
  /** Authenticated at the IdP but the email is not on the admission list: every /api call 403s. */
  notAdmitted = $state(false);
  /** True while the bundled dev IdP is in use, so the demo banner shows (never in a real deployment). */
  devAuth = $state(false);

  get email(): string {
    return this.me?.email ?? this.user?.profile.email ?? this.user?.profile.sub ?? "";
  }

  get role(): string | null {
    return this.me?.role ?? null;
  }

  private get capabilities(): string[] {
    return this.me?.capabilities ?? [];
  }

  /** Does the signed-in user hold a capability? Scope is accepted for forward-compatibility but not yet
      enforced (instance-scoped roles only in this MVP). */
  can(capability: Capability, _scope?: Scope): boolean {
    return this.capabilities.includes(capability);
  }

  /** Any of the admin-surface capabilities: gates the sidebar Admin group (README section 4). */
  get isAdminSurface(): boolean {
    return this.can("ManageUsers") || this.can("AdministerInstance");
  }

  /** Load the backend identity once signed in. A 403 means authenticated-but-not-admitted (README 5, panel A). */
  async loadMe(): Promise<void> {
    const { data, response } = await api.GET("/api/me");
    if (data) {
      this.me = data;
      this.notAdmitted = false;
    } else if (response.status === 403) {
      this.me = null;
      this.notAdmitted = true;
    } else {
      this.me = null;
      this.notAdmitted = false;
    }
  }

  reset(): void {
    this.user = null;
    this.me = null;
    this.notAdmitted = false;
  }
}

export const session = new SessionState();
