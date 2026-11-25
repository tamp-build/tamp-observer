import { describe, expect, it } from "vitest";
import { toPayload } from "./sink";
import type { AppError, ErrorKind } from "./stores/errors.svelte";

// tamp-observer#14: this function had `level: e.kind === "console" ? "error" : "error"` -- a
// conditional that returned the same value whichever way it went, so every client event reached the
// server as ERROR and the server's WARN path was unreachable.
//
// An exhaustive Record<ErrorKind, ...> now makes a missing kind a compile error, but that only
// catches an OMISSION. It would not catch someone setting every value to "error" again, which is
// precisely the original bug. These assert the actual values.

const error = (kind: ErrorKind, over: Partial<AppError> = {}): AppError => ({
  kind,
  context: "test",
  message: "boom",
  at: "2026-11-25T12:00:00.000Z",
  ...over,
});

describe("toPayload level mapping", () => {
  it("reports console-sourced events at warn", () => {
    // Only console.error is patched, so a "console" event is app code choosing to log --
    // not a failure the app suffered. Reporting it as ERROR made log noise indistinguishable
    // from a real fault, which is the thing this product exists to prevent.
    expect(toPayload(error("console")).level).toBe("warn");
  });

  it("reports every other kind at error", () => {
    const kinds: ErrorKind[] = [
      "network", "timeout", "server", "client", "render", "script", "unhandled",
    ];
    for (const kind of kinds) {
      expect(toPayload(error(kind)).level, kind).toBe("error");
    }
  });

  it("does not collapse to a single level — the #14 regression", () => {
    // The guard with teeth: if someone sets every entry to the same value again, this fails.
    const levels = new Set(
      (["console", "network", "server", "unhandled"] as ErrorKind[]).map((k) => toPayload(error(k)).level),
    );
    expect(levels.size).toBeGreaterThan(1);
  });
});

describe("toPayload passthrough", () => {
  it("carries the discriminator and the diagnostic fields through", () => {
    const p = toPayload(error("server", { url: "/api/x", status: 503, stack: "at foo" }));

    expect(p.kind).toBe("server");
    expect(p.message).toBe("boom");
    expect(p.url).toBe("/api/x");
    expect(p.status).toBe(503);
    expect(p.stack).toBe("at foo");
  });

  it("parses the captured timestamp", () => {
    expect(toPayload(error("render")).atUnixMs).toBe(Date.parse("2026-11-25T12:00:00.000Z"));
  });

  it("falls back to now when the timestamp is unparseable rather than sending NaN", () => {
    const before = Date.now();
    const p = toPayload(error("render", { at: "not a date" }));

    expect(Number.isNaN(p.atUnixMs)).toBe(false);
    expect(p.atUnixMs).toBeGreaterThanOrEqual(before);
  });
});
