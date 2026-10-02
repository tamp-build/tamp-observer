using Marten;
using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// Configures the Marten <see cref="StoreOptions"/> for the Postgres baseline tier (ADR 0005): the
/// document registrations and the natural-key unique indexes that back the evaluator's
/// upsert-by-natural-key dedup (ADR 0008). This is the per-engine translation for the Postgres
/// provider in the capability-based abstraction (ADR 0006); other tiers get their own configuration.
/// </summary>
public static class ObserverStoreConfiguration
{
    /// <summary>The Postgres schema all observer entity documents live in.</summary>
    public const string SchemaName = "observer";

    /// <summary>
    /// Apply the observer schema (connection, document registrations, indexes) to a Marten
    /// <see cref="StoreOptions"/>.
    /// </summary>
    public static void Configure(StoreOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        options.Connection(connectionString);
        options.DatabaseSchemaName = SchemaName;

        // Project is the trust root: its Key is the unique, human-granted ingestion identity (ADR 0007).
        options.Schema.For<Project>()
            .UniqueIndex(x => x.Key);

        // Service identity is service.name scoped within a Project (ADR 0007). service.namespace
        // participation is still open (ADR 0007 §4), so it is not part of this index yet.
        options.Schema.For<Service>()
            .UniqueIndex(x => x.ProjectId, x => x.ServiceName);

        // Environment is unique by name within a Project (ADR 0007); it is the capture-policy scope.
        options.Schema.For<DeploymentEnvironment>()
            .UniqueIndex(x => x.ProjectId, x => x.Name);

        // Version is unique per-Service by its (free-form) string, and ordered by the server-assigned
        // monotonic Sequence (ADR 0008), which gets its own index for ordered scans.
        options.Schema.For<ServiceVersion>()
            .UniqueIndex(x => x.ServiceId, x => x.VersionString)
            .Index(x => x.Sequence);

        // Area is a per-project managed vocabulary; names are unique within a Project (ADR 0007).
        options.Schema.For<Area>()
            .UniqueIndex(x => x.ProjectId, x => x.Name);

        // Quarantine custody store (ADR 0004 section 5): queryable by receipt and arrival time.
        options.Schema.For<QuarantinedEvent>()
            .Index(x => x.ReceiptId)
            .Index(x => x.QuarantinedAt);

        // Promoted telemetry (ADR 0004 admit path). Indexed for the common slice/correlation reads:
        // by entity (Service, Version), by trace, and by time.
        options.Schema.For<IngestedSpan>()
            .Index(x => x.ServiceId)
            .Index(x => x.VersionId)
            .Index(x => x.TraceId)
            .Index(x => x.StartUnixNano);

        options.Schema.For<IngestedLog>()
            .Index(x => x.ServiceId)
            .Index(x => x.VersionId)
            .Index(x => x.TraceId)
            .Index(x => x.TimeUnixNano);

        // Issue model (ADR 0015): one Issue per (Project, Service, Fingerprint); queried by status/recency.
        options.Schema.For<Issue>()
            .UniqueIndex(x => x.ProjectId, x => x.ServiceId, x => x.Fingerprint)
            .Index(x => x.Status)
            .Index(x => x.LastSeenAtUtc);

        // Installation-wide settings singleton, incl. enforcement posture (ADR 0002).
        options.Schema.For<InstanceSettings>();

        // Native RBAC grants (ADR 0013): looked up by subject.
        options.Schema.For<RoleAssignment>()
            .Index(x => x.SubjectId);

        // Admission list (ADR 0013): pre-registered identities, unique by normalized email.
        options.Schema.For<AllowedIdentity>()
            .UniqueIndex(x => x.Email);

        // Replay session metadata (ADR 0010): one per (Project, client SessionId); listed by recency. The
        // payload firehose is NOT here; it lives in the blob store.
        options.Schema.For<ReplaySession>()
            .UniqueIndex(x => x.ProjectId, x => x.SessionId)
            .Index(x => x.LastEventAtUtc);

        // Symbol artifacts (ADR 0017): one per (Project, Service, Version, generated file). Explicit index
        // name because the auto-generated one exceeds Postgres's 63-char identifier limit.
        options.Schema.For<SymbolArtifact>()
            .UniqueIndex(Marten.Schema.UniqueIndexType.Computed, "uq_symbol_artifact", x => x.ProjectId, x => x.ServiceId, x => x.VersionId, x => x.GeneratedFile);
    }
}
