import { describe, expect, it, vi, afterEach } from "vitest";
import {
  bytes,
  int64,
  issueStatusLabel,
  nanosToTime,
  severityClass,
  severityLabel,
  timeAgo,
} from "./format";

// The formatting and enum-mapping layer. Pure functions over values that arrive from the API, so the
// interesting cases are all the shapes the API can actually produce: openapi-typescript emits
// `number | string` for int64, fields are optional, and OTLP severity numbers are banded rather than
// enumerated. The boundaries are where these go wrong, so that is what is pinned.

describe("int64", () => {
  it("accepts both shapes openapi-typescript emits for an int64", () => {
    expect(int64(42)).toBe(42);
    expect(int64("42")).toBe(42);
  });

  it("treats absent as zero rather than NaN", () => {
    // NaN would propagate silently through bytes()/severityLabel() and render as "NaN".
    expect(int64(null)).toBe(0);
    expect(int64(undefined)).toBe(0);
  });
});

describe("issueStatusLabel", () => {
  it("maps the backend's integers to the four design labels", () => {
    expect(issueStatusLabel(0)).toBe("Unresolved");
    expect(issueStatusLabel(1)).toBe("Resolved");
    expect(issueStatusLabel(2)).toBe("Muted"); // Ignored on the wire, Muted in the UI
    expect(issueStatusLabel(3)).toBe("Regressed");
  });

  it("falls back to Unresolved for absent or unknown values", () => {
    // A new status added server-side must not render blank.
    expect(issueStatusLabel(undefined)).toBe("Unresolved");
    expect(issueStatusLabel(99)).toBe("Unresolved");
  });
});

describe("bytes", () => {
  it("scales through the units", () => {
    expect(bytes(0)).toBe("0 B");
    expect(bytes(512)).toBe("512 B");
    expect(bytes(1024)).toBe("1.0 KB");
    expect(bytes(1536)).toBe("1.5 KB");
    expect(bytes(1024 * 1024)).toBe("1.0 MB");
    expect(bytes(1024 ** 3)).toBe("1.0 GB");
  });

  it("drops the decimal once the number is large enough not to need it", () => {
    // The `n < 10` branch: 24 MB reads better than 24.0 MB.
    expect(bytes(24 * 1024 ** 2)).toBe("24 MB");
  });

  it("stops at the largest unit rather than running off the end of the array", () => {
    // Without the `i < units.length - 1` guard this would index past TB and print "undefined".
    expect(bytes(5 * 1024 ** 5)).toContain("TB");
  });

  it("treats non-positive and absent as zero", () => {
    expect(bytes(-1)).toBe("0 B");
    expect(bytes(undefined)).toBe("0 B");
  });
});

describe("severityLabel", () => {
  it("maps the exact OTLP band numbers", () => {
    expect(severityLabel(1)).toBe("TRACE");
    expect(severityLabel(5)).toBe("DEBUG");
    expect(severityLabel(9)).toBe("INFO");
    expect(severityLabel(13)).toBe("WARN");
    expect(severityLabel(17)).toBe("ERROR");
    expect(severityLabel(21)).toBe("FATAL");
  });

  it("rounds a within-band number down to its band", () => {
    // OTLP allows 17..20 for ERROR; all four must read ERROR, not SEV18.
    expect(severityLabel(18)).toBe("ERROR");
    expect(severityLabel(20)).toBe("ERROR");
    expect(severityLabel(14)).toBe("WARN");
  });

  it("falls back to SEVn for a number outside the known bands", () => {
    expect(severityLabel(99)).toBe("SEV99");
  });

  it("renders absent as a dash, not a level", () => {
    expect(severityLabel(undefined)).toBe("-");
    expect(severityLabel(0)).toBe("-");
  });
});

describe("severityClass", () => {
  it("buckets at the ERROR and WARN thresholds", () => {
    expect(severityClass(17)).toBe("lvl-err");
    expect(severityClass(21)).toBe("lvl-err");
    expect(severityClass(13)).toBe("lvl-warn");
    expect(severityClass(16)).toBe("lvl-warn");
    expect(severityClass(9)).toBe("lvl-info");
    expect(severityClass(undefined)).toBe("lvl-info");
  });
});

describe("timeAgo", () => {
  afterEach(() => vi.useRealTimers());

  const at = (iso: string, now: string) => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(now));
    return timeAgo(iso);
  };

  it("steps through the units", () => {
    expect(at("2026-11-25T12:00:00Z", "2026-11-25T12:00:30Z")).toBe("30s ago");
    expect(at("2026-11-25T12:00:00Z", "2026-11-25T12:05:00Z")).toBe("5m ago");
    expect(at("2026-11-25T12:00:00Z", "2026-11-25T14:00:00Z")).toBe("2h ago");
    expect(at("2026-11-22T12:00:00Z", "2026-11-25T12:00:00Z")).toBe("3d ago");
  });

  it("clamps a future timestamp to 0 rather than printing a negative age", () => {
    // Clock skew between the server and the browser is normal; "-5s ago" is not.
    expect(at("2026-11-25T12:00:05Z", "2026-11-25T12:00:00Z")).toBe("0s ago");
  });

  it("renders absent and unparseable as a dash", () => {
    expect(timeAgo(undefined)).toBe("-");
    expect(timeAgo("not a date")).toBe("-");
  });
});

describe("nanosToTime", () => {
  it("renders absent as a dash rather than the epoch", () => {
    // int64(undefined) is 0, and 0 nanos would otherwise format as 1970.
    expect(nanosToTime(undefined)).toBe("-");
    expect(nanosToTime(0)).toBe("-");
  });

  it("converts nanoseconds to a time string", () => {
    const ms = Date.UTC(2026, 10, 25, 12, 34, 56);
    expect(nanosToTime(ms * 1_000_000)).toBe(new Date(ms).toLocaleTimeString());
  });
});
