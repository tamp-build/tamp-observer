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
    }
}
