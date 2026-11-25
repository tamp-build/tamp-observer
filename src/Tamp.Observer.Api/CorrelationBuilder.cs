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

/// <summary>Assembles a <see cref="CorrelationView"/> from a resolved occurrence, enriching the trace summary,
/// logs-on-trace and session replay. Shared by the issue/trace/session correlation endpoints (ADR 0014).</summary>
public static class CorrelationBuilder
{
    public static async Task<CorrelationView> BuildAsync(
        Guid projectId, IssueOccurrence? occ, Guid? issueId, string? errorType,
        IObservabilityStore store, IReplaySessionStore replays, CancellationToken ct)
    {
        if (occ is null)
            return new CorrelationView(issueId, errorType, null, null, null, null, null, null, null);

        TraceSummary? trace = null;
        LogsOnTrace? logs = null;
        if (!string.IsNullOrEmpty(occ.TraceId))
        {
            var view = await store.GetTraceAsync(projectId, occ.TraceId, ct);
            if (view.Spans.Count > 0)
            {
                var minStart = view.Spans.Min(s => s.StartUnixNano);
                var maxEnd = view.Spans.Max(s => s.EndUnixNano);
                // The earliest-starting span is the most robust root for display (a partial trace may be missing
                // the true root's parent link).
                var root = view.Spans.OrderBy(s => s.StartUnixNano).First();
                trace = new TraceSummary(
                    root.Name, maxEnd - minStart, view.Spans.Count,
                    view.Spans.Select(s => s.ServiceId).Distinct().Count(),
                    view.Spans.Count(s => s.StatusCode == 2));
            }
            if (view.Logs.Count > 0)
            {
                var top = view.Logs.Where(l => l.SeverityNumber >= 17).Select(l => l.Body).FirstOrDefault(b => !string.IsNullOrEmpty(b))
                          ?? view.Logs.Select(l => l.Body).FirstOrDefault(b => !string.IsNullOrEmpty(b));
                logs = new LogsOnTrace(
                    view.Logs.Count,
                    view.Logs.Count(l => l.SeverityNumber >= 17),
                    view.Logs.Count(l => l.SeverityNumber is >= 13 and < 17),
                    top);
            }
        }

        ReplayLink? replay = null;
        if (!string.IsNullOrEmpty(occ.SessionId))
        {
            var session = await replays.FindAsync(projectId, occ.SessionId, ct);
            replay = new ReplayLink(occ.SessionId, session is not null, session?.EventCount ?? 0);
        }

        return new CorrelationView(
            issueId, errorType, occ.AtUnixNano, occ.TraceId, occ.SpanId, occ.SessionId, trace, logs, replay);
    }
}
