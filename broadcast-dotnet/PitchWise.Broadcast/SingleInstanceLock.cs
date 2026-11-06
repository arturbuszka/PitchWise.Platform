using Npgsql;

namespace PitchWise.Broadcast;

/// <summary>
/// Two services would both send to each destination, and a destination takes one sender per key. A Postgres
/// advisory lock is held by a connection, so it goes away with the service that held it.
/// </summary>
public sealed class SingleInstanceLock(BroadcastSettings settings) : IAsyncDisposable
{
    // An arbitrary number that only this service uses.
    private const long LockId = 7203418862L;

    private NpgsqlConnection? _connection;

    /// <summary>
    /// Whether this instance holds the lock. False means another one does. Throws if the database can't be
    /// reached, which says nothing about who should be sending.
    /// </summary>
    public async Task<bool> HoldAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            try
            {
                await using var ping = new NpgsqlCommand("SELECT 1", _connection);
                await ping.ExecuteScalarAsync(ct);
                return true;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                await DisposeAsync();
            }
        }

        var connection = new NpgsqlConnection(settings.DatabaseConnection);
        try
        {
            await connection.OpenAsync(ct);
            await using var acquire = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockId})", connection);
            if (await acquire.ExecuteScalarAsync(ct) is true)
            {
                _connection = connection;
                return true;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _connection = null;
    }
}
