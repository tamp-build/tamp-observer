// A tiny history-API router. The backend serves index.html as the SPA fallback (MapFallbackToFile), so deep
// links like /p/:projectId/issues resolve on refresh. Routes are matched against a small table; the first
// match wins. Global filter state rides the query string (see stores/filters) and is preserved across
// navigations unless explicitly replaced.

export interface Route {
  /** The matched route name, e.g. "issues", "issue-detail", "trace". */
  name: string;
  /** Path params captured from the pattern (e.g. { projectId, issueId }). */
  params: Record<string, string>;
  /** The current pathname. */
  path: string;
}

interface Pattern {
  name: string;
  /** Segments: a leading ":" marks a param. */
  segments: string[];
}

// Order matters: more specific patterns first.
const patterns: Pattern[] = [
  { name: "home", segments: [] },
  { name: "not-admitted", segments: ["not-admitted"] },
  { name: "admin-users", segments: ["admin", "users"] },
  { name: "admin-channels", segments: ["admin", "channels"] },
  { name: "admin-enforcement", segments: ["admin", "enforcement"] },
  { name: "admin-storage", segments: ["admin", "storage"] },
  { name: "issue-detail", segments: ["p", ":projectId", "issues", ":issueId"] },
  { name: "issues", segments: ["p", ":projectId", "issues"] },
  { name: "trace", segments: ["p", ":projectId", "traces", ":traceId"] },
  { name: "replay-session", segments: ["p", ":projectId", "replay", ":sessionId"] },
  { name: "replay", segments: ["p", ":projectId", "replay"] },
  { name: "logs", segments: ["p", ":projectId", "logs"] },
  { name: "alerts", segments: ["p", ":projectId", "alerts"] },
  { name: "overview", segments: ["p", ":projectId"] },
];

function match(pathname: string): Route {
  const parts = pathname.split("/").filter((s) => s.length > 0);
  for (const pattern of patterns) {
    if (pattern.segments.length !== parts.length) continue;
    const params: Record<string, string> = {};
    let ok = true;
    for (let i = 0; i < pattern.segments.length; i++) {
      const seg = pattern.segments[i];
      if (seg.startsWith(":")) {
        params[seg.slice(1)] = decodeURIComponent(parts[i]);
      } else if (seg !== parts[i]) {
        ok = false;
        break;
      }
    }
    if (ok) return { name: pattern.name, params, path: pathname };
  }
  return { name: "not-found", params: {}, path: pathname };
}

class Router {
  route = $state<Route>(match(window.location.pathname));

  constructor() {
    window.addEventListener("popstate", () => {
      this.route = match(window.location.pathname);
    });
  }

  /** Navigate to a path, pushing history. Pass preserveQuery to keep the current query string (global filters). */
  navigate(path: string, opts: { replace?: boolean; preserveQuery?: boolean } = {}): void {
    let target = path;
    if (opts.preserveQuery && window.location.search) {
      target += (path.includes("?") ? "&" : "?") + window.location.search.slice(1);
    }
    if (opts.replace) {
      window.history.replaceState({}, "", target);
    } else {
      window.history.pushState({}, "", target);
    }
    this.route = match(window.location.pathname);
  }

  /** Project home for a given id, carrying the current filters forward. */
  projectHref(projectId: string, suffix = ""): string {
    return `/p/${encodeURIComponent(projectId)}${suffix}`;
  }
}

export const router = new Router();

/** Intercept clicks on internal links so <a href> navigates without a full page load. */
export function link(node: HTMLAnchorElement) {
  function onClick(e: MouseEvent) {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    const href = node.getAttribute("href");
    if (!href || !href.startsWith("/")) return;
    e.preventDefault();
    router.navigate(href, { preserveQuery: node.dataset.keepFilters === "true" });
  }
  node.addEventListener("click", onClick);
  return {
    destroy() {
      node.removeEventListener("click", onClick);
    },
  };
}
