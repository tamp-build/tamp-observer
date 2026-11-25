using System.Text;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Observer.Alerting;
using Tamp.Observer.Connector.AspNetCore;
using Tamp.Observer.Domain;
using Tamp.Observer.Replay.FileBlob;
using Tamp.Observer.Storage.Abstractions;
using Tamp.Observer.Storage.ClickHouse;
using Tamp.Observer.Storage.DuckDb;
using Tamp.Observer.Storage.Postgres;

namespace Tamp.Observer.Api;

/// <summary>Who the caller is and what they can do (ADR 0013): the subject + email the IdP asserted, whether
/// they are admitted, their role, and the capability verbs that drive the UI's gating.</summary>
public sealed record MeResponse(
    string SubjectId, string Email, bool Admitted, string? Role, IReadOnlyList<string> Capabilities);

/// <summary>A project in the switcher.</summary>
public sealed record ProjectSummary(Guid Id, string Key, string Name);

public sealed record ServiceSummary(Guid Id, string ServiceName, string? Namespace);

/// <summary>A discovered Environment for the trace/header env tag (TOBS-28).</summary>
public sealed record EnvironmentSummary(Guid Id, string Name);

/// <summary>A discovered Service version for the trace/header version tag (TOBS-28). Sequence is the server
/// ordering key (ADR 0008); VersionString is the display label.</summary>
public sealed record VersionSummary(Guid Id, Guid ServiceId, string VersionString, long Sequence);

public sealed record IssueCounts(int Unresolved, int Regressed, int Resolved, int Muted, int Total);

// Correlation walk (ADR 0014): the thread linking an error occurrence to its trace, logs and session. Resolvable
// from any anchor (issue, trace or session), so the same view drives the walk on every surface. Fields are
// nullable because any leg of the thread may be absent (a client error has no server trace; a worker job has no
// session).
public sealed record CorrelationView(
    Guid? IssueId, string? ErrorType, long? AtUnixNano, string? TraceId, string? SpanId, string? SessionId,
    TraceSummary? Trace, LogsOnTrace? Logs, ReplayLink? Replay);

public sealed record TraceSummary(
    string RootOperation, long DurationNano, int SpanCount, int ServiceCount, int ErrorSpanCount);

public sealed record LogsOnTrace(int Total, int ErrorCount, int WarnCount, string? TopMessage);

public sealed record ReplayLink(string SessionId, bool Available, int EventCount);

/// <summary>The instance enforcement posture (ADR 0002).</summary>
public sealed record EnforcementView(string Mode, bool Locked);

/// <summary>A pre-registered identity (ADR 0013).</summary>
public sealed record UserView(string Email, string Role, DateTimeOffset CreatedAtUtc);

/// <summary>Pre-register an email with a role.</summary>
public sealed record AllowUserRequest(string Email, string? Role);

/// <summary>Which storage tiers this instance runs (ADR 0005).</summary>
public sealed record StorageHealth(string WriteStore, string ReadStore, bool ClickHouseConfigured);

/// <summary>Set an issue's status (ADR 0015).</summary>
public sealed record IssueStatusRequest(string Status, long? ResolvedInVersionSequence);

/// <summary>An alert rule (ADR 0016).</summary>
public sealed record AlertRuleView(string Id, string Name, bool Enabled);

/// <summary>A fired alert (history; not persisted yet).</summary>
public sealed record AlertEventView(string Kind, Guid IssueId, string Title, DateTimeOffset AtUtc);

/// <summary>Alert rules + recent history for a project.</summary>
public sealed record AlertsView(IReadOnlyList<AlertRuleView> Rules, IReadOnlyList<AlertEventView> History);

/// <summary>Runtime OIDC settings the SPA fetches at startup (ADR 0014), so the image is deployment-portable.</summary>
public sealed record SpaConfig(
    string OidcAuthority, string OidcAudience, string OidcClientId, string ClientProjectKey, string Environment);

/// <summary>One browser-captured event the SPA ships to <c>/ingest/client</c>.</summary>
public sealed record ClientEvent(
    string? Kind, string? Level, string Message, string? Url, int? Status, string? Stack, long? AtUnixMs);

/// <summary>A batch of browser events plus the session context used to correlate them (ADR 0010/0018).</summary>
public sealed record ClientTelemetryRequest(string? SessionId, string? UserAgent, ClientEvent[] Events);

/// <summary>One delivered replay chunk from the browser SDK (ADR 0010): a batch of rrweb events plus the
/// session identity and first-chunk context. <see cref="Events"/> is the raw rrweb events JSON array.</summary>
public sealed record ReplayChunkRequest(string SessionId, string? StartUrl, string? UserAgent, JsonElement Events);
