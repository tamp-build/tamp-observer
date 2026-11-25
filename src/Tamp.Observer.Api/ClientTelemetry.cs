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

/// <summary>Builds OTLP/JSON logs from a browser telemetry batch so the normal collector->evaluator->store path
/// ingests them like any other logs. String-valued attributes keep the JSON robust across the int64 wire rules.</summary>
public static class ClientTelemetry
{
    public static string BuildOtlpLogs(string projectKey, string service, string env, ClientTelemetryRequest body)
    {
        static object Attr(string k, string v) => new { key = k, value = new { stringValue = v } };
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var records = (body.Events ?? []).Select(e =>
        {
            var attrs = new List<object>();
            if (!string.IsNullOrEmpty(body.SessionId)) attrs.Add(Attr("tamp.session.id", body.SessionId!));
            if (!string.IsNullOrEmpty(e.Kind)) attrs.Add(Attr("client.kind", e.Kind!));
            if (!string.IsNullOrEmpty(e.Url)) attrs.Add(Attr("url.full", e.Url!));
            if (e.Status is int s) attrs.Add(Attr("http.response.status_code", s.ToString()));
            if (!string.IsNullOrEmpty(e.Stack)) attrs.Add(Attr("exception.stacktrace", e.Stack!));
            if (!string.IsNullOrEmpty(body.UserAgent)) attrs.Add(Attr("user_agent.original", body.UserAgent!));

            var warn = string.Equals(e.Level, "warn", StringComparison.OrdinalIgnoreCase);
            return new
            {
                timeUnixNano = ((e.AtUnixMs ?? nowMs) * 1_000_000L).ToString(),
                severityNumber = warn ? 13 : 17,
                severityText = warn ? "WARN" : "ERROR",
                body = new { stringValue = e.Message ?? string.Empty },
                attributes = attrs,
            };
        }).ToList();

        var doc = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = new[]
                        {
                            Attr("tamp.project.key", projectKey),
                            Attr("service.name", service),
                            Attr("deployment.environment", env),
                        },
                    },
                    scopeLogs = new[]
                    {
                        new { scope = new { name = "tamp-observer-web" }, logRecords = records },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(doc);
    }
}
