import { errors, errorMessage } from "./stores/errors.svelte";

// The client observability adapter: installed once, intrusive by design. It hooks the lowest-level browser
// primitives so every failure is captured no matter how the calling code is written. Nothing "programs toward"
// it; a surface that forgets all error handling is still fully covered.
//
// Coverage:
//   - window.fetch: every non-2xx and every network/timeout failure is captured. For same-origin API calls a
//     failure is NORMALIZED into a non-ok Response (not a throw), so openapi-fetch and components take their
//     ordinary !ok path and can never hang on an un-awaited rejection.
//   - XMLHttpRequest: errors/timeouts captured (behaviour unchanged) for any library that uses XHR.
//   - window error + unhandledrejection: sync throws, async rejections, and resource load failures.
//   - render errors are caught by the root <svelte:boundary>.
//
// It is transport-agnostic: everything lands in the errors store. Forwarding to a backend is a separate,
// pluggable sink; none of that coupling lives here.

const DEFAULT_TIMEOUT_MS = 15000;

function pathOf(url: string): string {
  try {
    return new URL(url, location.origin).pathname;
  } catch {
    return url;
  }
}

function isSameOriginApi(url: string): boolean {
  try {
    const u = new URL(url, location.origin);
    return u.origin === location.origin && u.pathname.startsWith("/api");
  } catch {
    return url.startsWith("/api");
  }
}

// Expected control flow, not errors worth capturing: pre-auth 401s, and the admission 403 on /api/me which the
// NotAdmitted UI handles.
function isExpected(url: string, status: number): boolean {
  if (status === 401) return true;
  if (status === 403 && url.includes("/api/me")) return true;
  return false;
}

function patchFetch(): void {
  const original = globalThis.fetch?.bind(globalThis);
  if (!original) return;

  globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = input instanceof Request ? input.url : String(input);
    const method = (init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();
    // Apply a default timeout to every request that did not set its own signal, so nothing can hang forever.
    const finalInit: RequestInit = init?.signal ? init : { ...init, signal: AbortSignal.timeout(DEFAULT_TIMEOUT_MS) };

    try {
      const res = await original(input as RequestInfo, finalInit);
      if (!res.ok && !isExpected(url, res.status)) {
        errors.capture({
          kind: res.status >= 500 ? "server" : "client",
          context: `${method} ${pathOf(url)}`,
          message: `${res.status} ${res.statusText}`.trim(),
          url,
          status: res.status,
        });
      }
      return res;
    } catch (e) {
      const timedOut = e instanceof DOMException && (e.name === "TimeoutError" || e.name === "AbortError");
      errors.capture({
        kind: timedOut ? "timeout" : "network",
        context: `${method} ${pathOf(url)}`,
        message: errorMessage(e),
        url,
      });
      // Normalize same-origin API failures into a non-ok Response so callers never hang on a throw. Other
      // callers (e.g. the OIDC library) keep their own throw-based handling.
      if (isSameOriginApi(url)) {
        return new Response(null, {
          status: timedOut ? 504 : 503,
          statusText: timedOut ? "Gateway Timeout" : "Service Unavailable",
        });
      }
      throw e;
    }
  };
}

function patchXhr(): void {
  const XHR = globalThis.XMLHttpRequest;
  if (!XHR) return;
  const open = XHR.prototype.open;
  const send = XHR.prototype.send;

  type Tagged = XMLHttpRequest & { __obs?: { method: string; url: string } };
  const openAny = open as (...a: unknown[]) => void;
  const sendAny = send as (...a: unknown[]) => void;

  XHR.prototype.open = function (this: Tagged, method: string, url: string | URL, ...rest: unknown[]) {
    this.__obs = { method, url: String(url) };
    return openAny.call(this, method, url, ...rest);
  } as typeof open;

  XHR.prototype.send = function (this: Tagged, ...args: unknown[]) {
    const meta = this.__obs;
    const ctx = meta ? `${meta.method.toUpperCase()} ${pathOf(meta.url)}` : "xhr";
    this.addEventListener("error", () =>
      errors.capture({ kind: "network", context: ctx, message: "XHR network error", url: meta?.url }),
    );
    this.addEventListener("timeout", () =>
      errors.capture({ kind: "timeout", context: ctx, message: "XHR timeout", url: meta?.url }),
    );
    this.addEventListener("load", () => {
      if (this.status >= 400 && !(meta && isExpected(meta.url, this.status))) {
        errors.capture({
          kind: this.status >= 500 ? "server" : "client",
          context: ctx,
          message: `${this.status} ${this.statusText}`.trim(),
          url: meta?.url,
          status: this.status,
        });
      }
    });
    return sendAny.apply(this, args);
  } as typeof send;
}

function formatArg(a: unknown): string {
  if (typeof a === "string") return a;
  if (a instanceof Error) return a.message || a.name;
  try {
    return JSON.stringify(a);
  } catch {
    return String(a);
  }
}

function patchConsole(): void {
  // Capture console.error as a logged error, then pass the original args straight to the native console so the
  // message still appears exactly as before. capture() echoes via its own native ref, so no recursion.
  const native = console.error.bind(console);
  console.error = (...args: unknown[]): void => {
    try {
      errors.capture(
        { kind: "console", context: "console.error", message: args.map(formatArg).join(" ") },
        { echo: false },
      );
    } catch {
      // Capturing must never break logging.
    }
    native(...args);
  };
}

function installGlobalHooks(): void {
  window.addEventListener(
    "error",
    (e: ErrorEvent) => {
      // Resource load failures (img/script/link) raise an error event whose target is the element.
      const target = e.target as (HTMLElement & { src?: string; href?: string }) | null;
      if (target && target !== (window as unknown) && (target.src || target.href)) {
        errors.capture({
          kind: "script",
          context: `resource ${target.tagName?.toLowerCase() ?? ""}`,
          message: `Failed to load ${target.src ?? target.href}`,
          url: target.src ?? target.href,
        });
        return;
      }
      errors.capture({ kind: "script", context: "window.error", message: e.error ? errorMessage(e.error) : e.message, stack: e.error?.stack });
    },
    true, // capture phase so resource errors (which do not bubble) are seen
  );

  window.addEventListener("unhandledrejection", (e: PromiseRejectionEvent) => {
    errors.capture({
      kind: "unhandled",
      context: "unhandledRejection",
      message: errorMessage(e.reason),
      stack: e.reason instanceof Error ? e.reason.stack : undefined,
    });
  });
}

/** Install the whole adapter. Call once, before the app mounts. */
export function installClientObservability(): void {
  patchFetch();
  patchXhr();
  patchConsole();
  installGlobalHooks();
}
