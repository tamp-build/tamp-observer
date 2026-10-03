// Runtime configuration from GET /config.json (one build serves every deployment). Cached after first load.
// Holds the OIDC settings and the client telemetry project key the SPA reports its own errors/replay under.

export interface AppConfig {
  oidcAuthority: string;
  oidcClientId: string;
  clientProjectKey: string;
  environment: string;
}

const DEFAULTS: AppConfig = {
  oidcAuthority: "",
  oidcClientId: "tamp-observer",
  clientProjectKey: "",
  environment: "dev",
};

let cached: AppConfig | null = null;

/** Load and cache /config.json. Safe to call repeatedly; never throws. */
export async function loadConfig(): Promise<AppConfig> {
  if (cached) return cached;
  try {
    const res = await fetch("/config.json", { cache: "no-store" });
    if (res.ok) {
      const raw = (await res.json()) as Partial<AppConfig>;
      cached = { ...DEFAULTS, ...raw };
      return cached;
    }
  } catch {
    // fall through to defaults
  }
  cached = { ...DEFAULTS };
  return cached;
}

/** The loaded config, or null before loadConfig() has resolved. */
export function config(): AppConfig | null {
  return cached;
}
