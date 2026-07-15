using System.Collections.Concurrent;
using KrakenDeploy.Server.Core.Domain.Audit;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>Recording <see cref="IAuditLog"/> test double — captures events for
/// assertions without a database round-trip.</summary>
public sealed class TestAuditLog : IAuditLog
{
    public sealed record Entry(
        string EventType, string? SubjectType, string? SubjectId, string? Details);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public Task RecordAsync(
        string eventType,
        string? subjectType = null,
        string? subjectId = null,
        string? subjectName = null,
        string? details = null,
        Guid? userId = null,
        string? userDisplay = null,
        CancellationToken ct = default)
    {
        Entries.Enqueue(new Entry(eventType, subjectType, subjectId, details));
        return Task.CompletedTask;
    }
}

/// <summary>Discarding <see cref="IAuditLog"/> for tests that don't assert audit.</summary>
public sealed class NullAuditLog : IAuditLog
{
    public Task RecordAsync(
        string eventType,
        string? subjectType = null,
        string? subjectId = null,
        string? subjectName = null,
        string? details = null,
        Guid? userId = null,
        string? userDisplay = null,
        CancellationToken ct = default) => Task.CompletedTask;
}
