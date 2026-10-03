// Frontend error capture: nothing should fail silently (the Logs each_key_duplicate crash span forever with
// no signal). Everything funnels here: render errors via <svelte:boundary>, data-load failures via the load
// guard, and anything else (async rejections, bad event handlers, resource loads) via the global hooks below.
// Captured errors are logged to the console, kept in a short ring, and surfaced in the UI (ErrorToast).

export interface AppError {
  context: string;
  message: string;
  at: string;
  stack?: string;
}

function toMessage(error: unknown): string {
  if (error == null) return "Unknown error";
  if (error instanceof Error) return error.message || error.name;
  if (typeof error === "string") return error;
  try {
    return JSON.stringify(error);
  } catch {
    return String(error);
  }
}

class ErrorLog {
  recent = $state<AppError[]>([]);
  last = $state<AppError | null>(null);

  report(context: string, error: unknown): AppError {
    const entry: AppError = {
      context,
      message: toMessage(error),
      at: new Date().toISOString(),
      stack: error instanceof Error ? error.stack : undefined,
    };
    this.recent = [entry, ...this.recent].slice(0, 20);
    this.last = entry;
    // Keep a real console trail; a future step can forward these to the backend as self-telemetry logs.
    console.error(`[tamp-observer] ${context}:`, error);
    return entry;
  }

  dismiss(): void {
    this.last = null;
  }
}

export const errors = new ErrorLog();

/** Install process-wide capture for errors that escape component boundaries. Call once at startup. */
export function installGlobalErrorCapture(): void {
  window.addEventListener("error", (e) => {
    // Resource load errors (img/script) also raise "error"; keep them, they are still signal.
    errors.report("window.error", e.error ?? e.message ?? "Script error");
  });
  window.addEventListener("unhandledrejection", (e) => {
    errors.report("unhandledRejection", e.reason);
  });
}
