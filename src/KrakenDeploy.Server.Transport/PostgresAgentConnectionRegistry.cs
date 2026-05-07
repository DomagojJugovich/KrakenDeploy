using System.Collections.Concurrent;
using Npgsql;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Postgres-backed implementation of <see cref="IAgentConnectionRegistry"/> using
/// an UNLOGGED table for fast writes with no WAL overhead. Suitable for a 2-node
/// HA pair sharing a single Postgres instance.
/// <para>
/// The table is truncated on construction (a server restart is a clean slate — all
/// agent connections are ephemeral). Each node reads and writes to the same table,
/// so sticky-session routing (via Caddy <c>lb_policy</c> or load-balancer config)
/// is required to ensure an agent always hits the node that holds its SignalR
/// connection.
/// </para>
/// </summary>
public sealed class PostgresAgentConnectionRegistry : IAgentConnectionRegistry, IDisposable
{
    private const string TableName = "agent_connections";

    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, Guid> _localByConnection = new();
    private readonly ConcurrentDictionary<Guid, string> _localByTarget = new();

    public PostgresAgentConnectionRegistry(string connectionString)
    {
        _connectionString = connectionString;

        // Fresh slate on node startup. All connections are ephemeral — agent
        // reconnections after a node restart go through the full connect flow.
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE \"{TableName}\"";
        cmd.ExecuteNonQuery();
    }

    public void Add(string connectionId, Guid targetId)
    {
        _localByConnection[connectionId] = targetId;
        _localByTarget[targetId] = connectionId;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO "{TableName}" (connection_id, target_id, connected_at_utc)
            VALUES (@cid, @tid, now())
            ON CONFLICT (connection_id) DO UPDATE
            SET target_id = @tid, connected_at_utc = now()
            """;
        cmd.Parameters.AddWithValue("cid", connectionId);
        cmd.Parameters.AddWithValue("tid", targetId);
        cmd.ExecuteNonQuery();
    }

    public bool TryRemove(string connectionId, out Guid targetId)
    {
        if (!_localByConnection.TryRemove(connectionId, out targetId))
        {
            return false;
        }

        _localByTarget.TryRemove(targetId, out _);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{TableName}\" WHERE connection_id = @cid";
        cmd.Parameters.AddWithValue("cid", connectionId);
        cmd.ExecuteNonQuery();
        return true;
    }

    public bool HasConnectionFor(Guid targetId)
        => _localByTarget.ContainsKey(targetId);

    public Guid? GetTargetId(string connectionId)
        => _localByConnection.TryGetValue(connectionId, out var id) ? id : null;

    public string? GetConnectionId(Guid targetId)
        => _localByTarget.TryGetValue(targetId, out var connId) ? connId : null;

    public int Count => _localByConnection.Count;

    public void Dispose()
    {
        // No-op: the table is UNLOGGED (truncated on Postgres restart) and
        // we truncate on construction. Connections are ephemeral by design.
    }

    private NpgsqlConnection OpenConnection()
    {
        var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
