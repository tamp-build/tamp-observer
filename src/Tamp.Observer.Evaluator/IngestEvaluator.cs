using Marten;
using Microsoft.Extensions.Logging;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Evaluator;

/// <summary>Outcome counts from draining the spool once.</summary>
public readonly record struct DrainStats(int Admitted, int Quarantined)
{
    public int Total => Admitted + Quarantined;
}

/// <summary>
/// The .NET evaluator (ADR 0004): consumes raw events from the spool, treats them as
/// well-formed-but-untrusted, adjudicates, and either admits (resolve + provision entities, promote
/// to the store) or rejects (quarantine with a reason). Every event leaves the spool for exactly one
/// of {store, quarantine}.
/// </summary>
public sealed class IngestEvaluator(IDocumentStore store, SpoolReader spool, ILogger<IngestEvaluator>? logger = null)
{
    private readonly IDocumentStore _store = store;
    private readonly SpoolReader _spool = spool;
    private readonly ILogger<IngestEvaluator>? _log = logger;

    /// <summary>Process every ready event currently in the spool.</summary>
    public async Task<DrainStats> DrainAsync(CancellationToken ct = default)
    {
        int admitted = 0, quarantined = 0;

        foreach (var item in _spool.ReadReady())
        {
            ct.ThrowIfCancellationRequested();
            if (await ProcessAsync(item, ct))
                admitted++;
            else
                quarantined++;

            SpoolReader.Remove(item);
        }

        return new DrainStats(admitted, quarantined);
    }

    /// <summary>Returns true if the event was admitted, false if quarantined.</summary>
    private async Task<bool> ProcessAsync(SpoolItem item, CancellationToken ct)
    {
        IReadOnlyList<ParsedResource> resources;
        try
        {
            resources = OtlpParser.Parse(item.Envelope.Signal, item.Payload);
        }
        catch (Exception ex)
        {
            await QuarantineAsync(item, $"unparseable OTLP payload: {ex.Message}", claimedProjectKey: null, ct);
            return false;
        }

        if (resources.Count == 0)
        {
            await QuarantineAsync(item, "no resource in payload", claimedProjectKey: null, ct);
            return false;
        }

        await using var session = _store.LightweightSession();

        // An event is admitted or quarantined atomically. Resolve the trust-root Project and service
        // grain for every resource first; if any resource fails, the whole event is quarantined and
        // nothing is stored (ADR 0004: exactly one of {store, quarantine}).
        var pending = new List<(ParsedResource Res, Project Project, string ServiceName)>(resources.Count);
        foreach (var res in resources)
        {
            var key = res.Attributes.Get(ResourceKeys.ProjectKey);
            if (string.IsNullOrEmpty(key))
            {
                await QuarantineAsync(item, $"missing {ResourceKeys.ProjectKey}", claimedProjectKey: null, ct);
                return false;
            }

            var project = await EntityResolver.FindProjectByKeyAsync(session, key, ct);
            if (project is null)
            {
                await QuarantineAsync(item, $"unknown project '{key}'", claimedProjectKey: key, ct);
                return false;
            }

            var serviceName = res.Attributes.Get(ResourceKeys.ServiceName);
            if (string.IsNullOrEmpty(serviceName))
            {
                await QuarantineAsync(item, $"missing {ResourceKeys.ServiceName}", claimedProjectKey: project.Key, ct);
                return false;
            }

            pending.Add((res, project, serviceName));
        }

        // Admit: provision discovered entities, then promote the signal bodies into the store.
        foreach (var (res, project, serviceName) in pending)
        {
            var attrs = res.Attributes;

            var service = await EntityResolver.ResolveServiceAsync(
                session, project.Id, serviceName, attrs.Get(ResourceKeys.ServiceNamespace), item.Envelope.ReceivedAt, ct);

            Guid? environmentId = null;
            var envName = attrs.Get(ResourceKeys.DeploymentEnvironment);
            if (!string.IsNullOrEmpty(envName))
            {
                var env = await EntityResolver.ResolveEnvironmentAsync(session, project.Id, envName, item.Envelope.ReceivedAt, ct);
                environmentId = env.Id;
            }

            var version = await EntityResolver.ResolveVersionAsync(
                session, project.Id, service.Id, attrs.Get(ResourceKeys.ServiceVersion), item.Envelope.ReceivedAt, ct);

            var instanceId = attrs.Get(ResourceKeys.ServiceInstanceId) ?? Synthetic.UnknownInstance;

            foreach (var s in res.Spans)
            {
                session.Store(new IngestedSpan
                {
                    ProjectId = project.Id,
                    ServiceId = service.Id,
                    EnvironmentId = environmentId,
                    VersionId = version.Id,
                    TraceId = s.TraceId,
                    SpanId = s.SpanId,
                    ParentSpanId = s.ParentSpanId,
                    Name = s.Name,
                    Kind = s.Kind,
                    StartUnixNano = s.StartUnixNano,
                    EndUnixNano = s.EndUnixNano,
                    DurationNano = s.EndUnixNano - s.StartUnixNano,
                    StatusCode = s.StatusCode,
                    StatusMessage = s.StatusMessage,
                    InstanceId = instanceId,
                    Attributes = new Dictionary<string, string>(s.Attributes),
                    ReceiptId = item.Envelope.ReceiptId,
                    ReceivedAt = item.Envelope.ReceivedAt,
                });
            }

            foreach (var l in res.Logs)
            {
                session.Store(new IngestedLog
                {
                    ProjectId = project.Id,
                    ServiceId = service.Id,
                    EnvironmentId = environmentId,
                    VersionId = version.Id,
                    TimeUnixNano = l.TimeUnixNano,
                    SeverityNumber = l.SeverityNumber,
                    SeverityText = l.SeverityText,
                    Body = l.Body,
                    TraceId = l.TraceId,
                    SpanId = l.SpanId,
                    InstanceId = instanceId,
                    Attributes = new Dictionary<string, string>(l.Attributes),
                    ReceiptId = item.Envelope.ReceiptId,
                    ReceivedAt = item.Envelope.ReceivedAt,
                });
            }
        }

        await session.SaveChangesAsync(ct);
        _log?.LogDebug("admitted receipt {ReceiptId} ({Signal})", item.Envelope.ReceiptId, item.Envelope.Signal);
        return true;
    }

    private async Task QuarantineAsync(SpoolItem item, string reason, string? claimedProjectKey, CancellationToken ct)
    {
        await using var session = _store.LightweightSession();
        session.Store(new QuarantinedEvent
        {
            ReceiptId = item.Envelope.ReceiptId,
            ReceivedAt = item.Envelope.ReceivedAt,
            QuarantinedAt = DateTimeOffset.UtcNow,
            Signal = item.Envelope.Signal,
            Source = item.Envelope.Source,
            Reason = reason,
            ClaimedProjectKey = claimedProjectKey,
            PayloadBytes = item.Payload.Length,
        });
        await session.SaveChangesAsync(ct);
        _log?.LogInformation("quarantined receipt {ReceiptId}: {Reason}", item.Envelope.ReceiptId, reason);
    }
}
