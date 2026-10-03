import type { IssueStatus } from "./components/ui/StatusPill.svelte";

// Shared formatting + enum mapping. The backend serializes IssueStatus as its integer (0 Unresolved, 1
// Resolved, 2 Ignored, 3 Regressed); the UI speaks the four design labels (Muted == Ignored).

export function issueStatusLabel(status: number | undefined): IssueStatus {
  switch (status) {
    case 1:
      return "Resolved";
    case 2:
      return "Muted";
    case 3:
      return "Regressed";
    default:
      return "Unresolved";
  }
}

/** Coerce an int64-that-may-be-a-string (openapi-typescript emits number | string) to a number. */
export function int64(v: number | string | null | undefined): number {
  if (v == null) return 0;
  return typeof v === "string" ? Number(v) : v;
}

/** Compact relative time from an ISO timestamp, e.g. "2h ago", "3d ago". */
export function timeAgo(iso: string | undefined): string {
  if (!iso) return "-";
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "-";
  const s = Math.max(0, Math.floor((Date.now() - then) / 1000));
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  const d = Math.floor(h / 24);
  return `${d}d ago`;
}

/** Nanosecond Unix time to a local time string. */
export function nanosToTime(nanos: number | string | undefined): string {
  const n = int64(nanos);
  if (!n) return "-";
  return new Date(n / 1_000_000).toLocaleTimeString();
}

const SEVERITY: Record<number, string> = {
  1: "TRACE",
  5: "DEBUG",
  9: "INFO",
  13: "WARN",
  17: "ERROR",
  21: "FATAL",
};

/** OTLP severity number to a level label (rounded down to the band). */
export function severityLabel(num: number | string | undefined): string {
  const n = int64(num);
  if (!n) return "-";
  const band = Math.floor((n - 1) / 4) * 4 + 1;
  return SEVERITY[band] ?? `SEV${n}`;
}

/** The CSS color token name for a severity, for log row coloring. */
export function severityClass(num: number | string | undefined): string {
  const n = int64(num);
  if (n >= 17) return "lvl-err";
  if (n >= 13) return "lvl-warn";
  return "lvl-info";
}
