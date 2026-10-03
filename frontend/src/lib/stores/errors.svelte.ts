// The client observability store: one place every failure lands. Capture is automatic (the fetch patch and
// global hooks in observability.ts feed this); components never have to wire it. This is the Sentry-style
// contract: install the adapter once, get maximum coverage, no per-call-site code.

export type ErrorKind =
  | "network"
  | "timeout"
  | "server"
  | "client"
  | "render"
  | "script"
  | "unhandled"
  | "console";

// Grab the native console methods at module load, before the observability adapter patches them. capture()
// echoes through these so logging can never re-enter the patched console and loop.
const nativeError = console.error.bind(console);

export interface AppError {
  kind: ErrorKind;
  context: string;
  message: string;
  at: string;
  url?: string;
  status?: number;
  stack?: string;
}

export function errorMessage(error: unknown): string {
  if (error == null) return "Unknown error";
  if (error instanceof Error) return error.message || error.name;
  if (typeof error === "string") return error;
  try {
    return JSON.stringify(error);
  } catch {
    return String(error);
  }
}

// Which captured errors get surfaced to the user (toast). 4xx are usually expected control flow the component
// already renders; server/network/timeout/render/script/unhandled are loud by default.
function shouldSurface(kind: ErrorKind, status?: number): boolean {
  if (kind === "client") return (status ?? 0) >= 500;
  return true;
}

class ErrorLog {
  /** Recent captured errors (newest first); the basis for an error log view and backend forwarding. */
  recent = $state<AppError[]>([]);
  /** The most recent surfaced error, shown by the toast. */
  last = $state<AppError | null>(null);
  /** Optional pluggable forwarder (e.g. the backend sink). Kept transport-agnostic: the store never knows the
      destination. */
  private sink: ((e: AppError) => void) | null = null;

  setSink(fn: ((e: AppError) => void) | null): void {
    this.sink = fn;
  }

  capture(entry: Omit<AppError, "at">, opts: { surface?: boolean; echo?: boolean } = {}): AppError {
    const full: AppError = { ...entry, at: new Date().toISOString() };
    this.recent = [full, ...this.recent].slice(0, 50);
    // echo defaults on; the console patch passes echo:false because it re-logs the original args itself.
    if (opts.echo ?? true) nativeError(`[tamp-observer] ${full.context}: ${full.message}`, full.status ?? "");
    const surface = opts.surface ?? shouldSurface(full.kind, full.status);
    if (surface && !this.isDuplicateOfLast(full)) this.last = full;
    // Forward to the sink if one is registered; it must never throw back into capture.
    if (this.sink) {
      try {
        this.sink(full);
      } catch {
        /* a broken sink must not break capture */
      }
    }
    return full;
  }

  private isDuplicateOfLast(e: AppError): boolean {
    const l = this.last;
    return !!l && l.context === e.context && l.message === e.message;
  }

  dismiss(): void {
    this.last = null;
  }
}

export const errors = new ErrorLog();
