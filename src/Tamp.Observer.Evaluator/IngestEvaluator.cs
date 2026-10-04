using Marten;
using Microsoft.Extensions.Logging;
using Tamp.Observer.Alerting;
using Tamp.Observer.Domain;
using Tamp.Observer.RawBucket.Abstractions;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Evaluator;

/// <summary>Outcome counts from draining the spool once.</summary>
public readonly record struct DrainStats(int Admitted, int Quarantined)
{
    public int Total => Admitted + Quarantined;
}

/// <summary>
/// The .NET evaluator (ADR 0004): consumes raw events from the spool, treats them as
/// well-formed-but-untrusted, adjudicates, and either admits (resolve + provision entities, then write
/// the batch through <see cref="IEventSink"/>) or rejects (quarantine with a reason). Every event leaves
/// the spool for exactly one of {store, quarantine}.
/// </summary>
public sealed partial class IngestEvaluator(
    IDocumentStore store,
    IRawBucketReader bucket,
    IEventSink sink,
    ILogger<IngestEvaluator> logger,
    IAlertDispatcher? alerts = null)
{
    private const int BatchSize = 500;

    private readonly IDocumentStore _store = store;
    private readonly IRawBucketReader _bucket = bucket;
    private readonly IEventSink _sink = sink;
    private readonly ILogger<IngestEvaluator> _log = logger;
    private readonly IAlertDispatcher? _alerts = alerts;

    [LoggerMessage(Level = LogLevel.Debug, Message = "admitted receipt {ReceiptId} ({Signal})")]
    private partial void LogAdmitted(string receiptId, string signal);

    [LoggerMessage(Level = LogLevel.Information, Message = "quarantined receipt {ReceiptId}: {Reason}")]
    private partial void LogQuarantined(string receiptId, string reason);

    /// <summary>Process every ready event currently in the raw bucket, in arrival order.</summary>
    public async Task<DrainStats> DrainAsync(CancellationToken ct = default)
    {
        int admitted = 0, quarantined = 0;

        while (true)
        {
            var items = await _bucket.ReadReadyAsync(BatchSize, ct);
            if (items.Count == 0)
                break;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (await ProcessAsync(item, ct))
                    admitted++;
                else
                    quarantined++;

                await _bucket.AcknowledgeAsync(item, ct);
            }

            if (items.Count < BatchSize)
                break;
        }

        return new DrainStats(admitted, quarantined);
    }

    /// <summary>Returns true if the event was admitted, false if quarantined.</summary>
    private async Task<bool> ProcessAsync(RawBucketItem item, CancellationToken ct)
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

        await using var read = _store.QuerySession();
        var resolver = new BatchResolver(read);
        var issueProjector = new IssueProjector(read);

        // Validate the trust-root Project and service grain for every resource first; if any resource
        // fails, the whole event is quarantined and nothing is written (atomic, ADR 0004).
        var pending = new List<(ParsedResource Res, Project Project, string ServiceName)>(resources.Count);
        foreach (var res in resources)
        {
            var key = res.Attributes.Get(ResourceKeys.ProjectKey);
            if (string.IsNullOrEmpty(key))
            {
                await QuarantineAsync(item, $"missing {ResourceKeys.ProjectKey}", claimedProjectKey: null, ct);
                return false;
            }

            var project = await resolver.FindProjectByKeyAsync(key, ct);
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

        // Resolve discovered entities and stamp the telemetry, accumulating a batch for the sink.
        var spans = new List<IngestedSpan>();
        var logs = new List<IngestedLog>();

        foreach (var (res, project, serviceName) in pending)
        {
            var attrs = res.Attributes;

            var service = await resolver.ResolveServiceAsync(
                project.Id, serviceName, attrs.Get(ResourceKeys.ServiceNamespace), item.Envelope.ReceivedAt, ct);

            Guid? environmentId = null;
            var envName = attrs.Get(ResourceKeys.DeploymentEnvironment);
            if (!string.IsNullOrEmpty(envName))
            {
                var env = await resolver.ResolveEnvironmentAsync(project.Id, envName, item.Envelope.ReceivedAt, ct);
                environmentId = env.Id;
            }

            var version = await resolver.ResolveVersionAsync(
                project.Id, service.Id, attrs.Get(ResourceKeys.ServiceVersion), item.Envelope.ReceivedAt, ct);

            var instanceId = attrs.Get(ResourceKeys.ServiceInstanceId) ?? Synthetic.UnknownInstance;

            foreach (var s in res.Spans)
            {
                // Stamp the Issue fingerprint onto error occurrences so an Issue links back to its latest span.
                string? fingerprint = null;
                if (s.StatusCode == 2)
                {
                    var et = s.Attributes.GetValueOrDefault(ResourceKeys.ExceptionType);
                    fingerprint = IssueFingerprint.Compute(service.Id, et ?? $"{s.Name}|{s.StatusMessage}");
                }

                spans.Add(new IngestedSpan
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
                    Fingerprint = fingerprint,
                    ReceiptId = item.Envelope.ReceiptId,
                    ReceivedAt = item.Envelope.ReceivedAt,
                });
            }

            foreach (var l in res.Logs)
            {
                string? fingerprint = null;
                if (l.SeverityNumber >= 17) // OTLP SEVERITY_NUMBER_ERROR
                {
                    var et = l.Attributes.GetValueOrDefault(ResourceKeys.ExceptionType);
                    var category = l.Attributes.GetValueOrDefault(ResourceKeys.LogCategory);
                    fingerprint = IssueFingerprint.Compute(service.Id, et ?? LogGrouping.Key(l.Body, category));
                }

                logs.Add(new IngestedLog
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
                    Fingerprint = fingerprint,
                    ReceiptId = item.Envelope.ReceiptId,
                    ReceivedAt = item.Envelope.ReceivedAt,
                });
            }

            // Project error signals into Issues (ADR 0015): ERROR-status spans and error-severity logs.
            foreach (var s in res.Spans)
            {
                if (s.StatusCode != 2)
                    continue;
                var errorType = s.Attributes.GetValueOrDefault(ResourceKeys.ExceptionType);
                var title = errorType ?? s.Name;
                var groupingKey = errorType ?? $"{s.Name}|{s.StatusMessage}";
                await issueProjector.ProjectAsync(project.Id, service.Id, version.Sequence, errorType, title, groupingKey, item.Envelope.ReceivedAt, ct);
            }

            foreach (var l in res.Logs)
            {
                if (l.SeverityNumber < 17) // OTLP SEVERITY_NUMBER_ERROR
                    continue;
                var errorType = l.Attributes.GetValueOrDefault(ResourceKeys.ExceptionType);
                var category = l.Attributes.GetValueOrDefault(ResourceKeys.LogCategory);
                // Group by error CLASS: the fingerprint is over the category + the literal-parameterized message, so
                // repeated occurrences collapse into one Issue (TOBS-42). Title keeps a readable sample line.
                var key = errorType ?? LogGrouping.Key(l.Body, category);
                var title = errorType ?? LogGrouping.Title(l.Body);
                await issueProjector.ProjectAsync(project.Id, service.Id, version.Sequence, errorType, title, key, item.Envelope.ReceivedAt, ct);
            }
        }

        var batch = new AdmittedBatch(
            resolver.NewServices, resolver.NewEnvironments, resolver.NewVersions, spans, logs,
            issueProjector.Touched.ToList());
        await _sink.WriteAsync(batch, ct);

        // Fan out alerts (new / regressed Issues) after the write succeeds, off the critical path.
        if (_alerts is not null && issueProjector.Alerts.Count > 0)
        {
            var instance = await read.LoadAsync<InstanceSettings>(InstanceSettings.SingletonId, ct) ?? new InstanceSettings();
            await _alerts.DispatchAsync(issueProjector.Alerts, EnforcementGate.Resolve(instance, null), ct);
        }

        LogAdmitted(item.Envelope.ReceiptId, item.Envelope.Signal);
        return true;
    }

    private async Task QuarantineAsync(RawBucketItem item, string reason, string? claimedProjectKey, CancellationToken ct)
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
        LogQuarantined(item.Envelope.ReceiptId, reason);
    }
}
