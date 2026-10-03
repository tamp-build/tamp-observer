using Tamp.Observer.Domain;

namespace Tamp.Observer.Storage.Abstractions;

/// <summary>
/// Read/write of the Issue grouping entity (ADR 0015). Issues always live in the Postgres system of record
/// (never the analytical tier), so this is a Marten-backed store rather than a per-engine translator like
/// <see cref="IObservabilityStore"/>.
/// </summary>
public interface IIssueStore
{
    /// <summary>List a project's issues, most-recently-seen first, optionally filtered by status and service.</summary>
    Task<IReadOnlyList<Issue>> ListAsync(
        Guid projectId, IssueStatus? status = null, Guid? serviceId = null, int limit = 100, CancellationToken ct = default);

    /// <summary>One issue by id within a project, or null.</summary>
    Task<Issue?> GetAsync(Guid projectId, Guid issueId, CancellationToken ct = default);

    /// <summary>Set an issue's status (resolve/reopen/ignore). Records the resolving version when provided.
    /// Returns false if the issue does not exist.</summary>
    Task<bool> SetStatusAsync(
        Guid projectId, Guid issueId, IssueStatus status, long? resolvedInVersionSequence = null, CancellationToken ct = default);
}
