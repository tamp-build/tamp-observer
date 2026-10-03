// Global filter state shared across every project surface: environment, time window, version (README section
// 4). These round-trip through the URL query string so a link reproduces exactly what the sender saw. The
// store reads the query on construction and writes it back (replaceState) whenever a filter changes.

export type TimeWindow = "1h" | "24h" | "7d" | "30d";

export interface Filters {
  env: string;
  window: TimeWindow;
  version: string | null;
}

const WINDOWS: TimeWindow[] = ["1h", "24h", "7d", "30d"];

/** Milliseconds for a window key, used to derive the [start,end) nanosecond range the API expects. */
export function windowMs(w: TimeWindow): number {
  switch (w) {
    case "1h":
      return 60 * 60 * 1000;
    case "24h":
      return 24 * 60 * 60 * 1000;
    case "7d":
      return 7 * 24 * 60 * 60 * 1000;
    case "30d":
      return 30 * 24 * 60 * 60 * 1000;
  }
}

function readQuery(): Filters {
  const q = new URLSearchParams(window.location.search);
  const w = q.get("window");
  return {
    env: q.get("env") ?? "prod",
    window: WINDOWS.includes(w as TimeWindow) ? (w as TimeWindow) : "24h",
    version: q.get("version"),
  };
}

class FilterState {
  env = $state("prod");
  window = $state<TimeWindow>("24h");
  version = $state<string | null>(null);

  constructor() {
    const f = readQuery();
    this.env = f.env;
    this.window = f.window;
    this.version = f.version;
  }

  /** The [start,end) range in Unix nanoseconds for the current window, ending now. */
  get rangeNanos(): { start: number; end: number } {
    const endMs = Date.now();
    const startMs = endMs - windowMs(this.window);
    return { start: startMs * 1_000_000, end: endMs * 1_000_000 };
  }

  setEnv(env: string): void {
    this.env = env;
    this.sync();
  }

  setWindow(w: TimeWindow): void {
    this.window = w;
    this.sync();
  }

  setVersion(version: string | null): void {
    this.version = version;
    this.sync();
  }

  /** Write the current filters back to the URL query string without a navigation. */
  private sync(): void {
    const q = new URLSearchParams(window.location.search);
    q.set("env", this.env);
    q.set("window", this.window);
    if (this.version) q.set("version", this.version);
    else q.delete("version");
    const search = q.toString();
    window.history.replaceState({}, "", window.location.pathname + (search ? "?" + search : ""));
  }
}

export const filters = new FilterState();
export { WINDOWS };
