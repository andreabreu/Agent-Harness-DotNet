using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace AgentPoc.Core;

/// <summary>Valor a ser enviado como jsonb a partir de uma string JSON.</summary>
public sealed record Jsonb(string Json);

/// <summary>Executa SQL no banco direto ou dentro de uma transacao (mesma API nos dois casos).</summary>
public interface IDbExecutor
{
    Task<int> ExecAsync(string sql, params (string Name, object? Value)[] ps);
    Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] ps);
    Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params (string Name, object? Value)[] ps);
    Task<Dictionary<string, object?>?> QueryOneAsync(string sql, params (string Name, object? Value)[] ps);
}

/// <summary>Acesso minimo ao Postgres (Npgsql puro, sem ORM).</summary>
public sealed class Db : IDbExecutor, IAsyncDisposable
{
    private readonly NpgsqlDataSource _ds;

    public Db(string connectionString)
    {
        _ds = NpgsqlDataSource.Create(connectionString);
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection conn, NpgsqlTransaction? tx, (string Name, object? Value)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in ps)
        {
            NpgsqlParameter p = value switch
            {
                null => new NpgsqlParameter(name, DBNull.Value),
                JsonNode node => new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = node.ToJsonString() },
                Jsonb j => new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = j.Json },
                _ => new NpgsqlParameter(name, value),
            };
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadAsync(NpgsqlCommand cmd)
    {
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] ps)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = Command(sql, conn, null, ps);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] ps)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = Command(sql, conn, null, ps);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params (string Name, object? Value)[] ps)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = Command(sql, conn, null, ps);
        return await ReadAsync(cmd);
    }

    public async Task<Dictionary<string, object?>?> QueryOneAsync(string sql, params (string Name, object? Value)[] ps) =>
        (await QueryAsync(sql, ps)).FirstOrDefault();

    /// <summary>Roda <paramref name="work"/> numa unica transacao: tudo grava junto ou nada grava (rollback em excecao).</summary>
    public async Task<T> InTransactionAsync<T>(Func<IDbExecutor, Task<T>> work)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var result = await work(new Transaction(conn, tx));
        await tx.CommitAsync();
        return result;
    }

    private sealed class Transaction : IDbExecutor
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction _tx;

        public Transaction(NpgsqlConnection conn, NpgsqlTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] ps)
        {
            await using var cmd = Command(sql, _conn, _tx, ps);
            return await cmd.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] ps)
        {
            await using var cmd = Command(sql, _conn, _tx, ps);
            var v = await cmd.ExecuteScalarAsync();
            return v is DBNull ? null : v;
        }

        public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params (string Name, object? Value)[] ps)
        {
            await using var cmd = Command(sql, _conn, _tx, ps);
            return await ReadAsync(cmd);
        }

        public async Task<Dictionary<string, object?>?> QueryOneAsync(string sql, params (string Name, object? Value)[] ps) =>
            (await QueryAsync(sql, ps)).FirstOrDefault();
    }

    public Task NotifyAsync(string channel, string payload) =>
        ExecAsync("select pg_notify(@ch, @p)", ("ch", channel), ("p", payload));

    /// <summary>
    /// Conexao dedicada que escuta canais do Postgres (LISTEN) e chama <paramref name="onNotification"/>
    /// com (canal, payload) a cada NOTIFY. Descartar o retorno encerra a escuta.
    /// </summary>
    public async Task<IAsyncDisposable> ListenAsync(IEnumerable<string> channels, Action<string, string> onNotification, CancellationToken ct)
    {
        var conn = await _ds.OpenConnectionAsync(ct);
        conn.Notification += (_, e) => onNotification(e.Channel, e.Payload);
        foreach (var channel in channels)
        {
            await using var cmd = new NpgsqlCommand($"listen {channel}", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new Listener(conn, ct);
    }

    private sealed class Listener : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly CancellationTokenSource _stop;
        private readonly Task _loop;

        public Listener(NpgsqlConnection conn, CancellationToken ct)
        {
            _conn = conn;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested) await _conn.WaitAsync(_stop.Token);
                }
                catch (OperationCanceledException) { /* encerrando */ }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop; } catch { /* conexao ja encerrada */ }
            await _conn.DisposeAsync();
            _stop.Dispose();
        }
    }

    public ValueTask DisposeAsync() => _ds.DisposeAsync();
}

/// <summary>Leitura tipada de linhas retornadas por Db.QueryAsync.</summary>
public static class Row
{
    private static object? V(Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) ? v : null;

    public static string S(this Dictionary<string, object?> r, string k) =>
        V(r, k) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    public static string? SN(this Dictionary<string, object?> r, string k) =>
        V(r, k) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    public static int I(this Dictionary<string, object?> r, string k) =>
        V(r, k) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;

    public static long L(this Dictionary<string, object?> r, string k) =>
        V(r, k) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0L;

    public static decimal D(this Dictionary<string, object?> r, string k) =>
        V(r, k) is { } v ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : 0m;

    public static float? FN(this Dictionary<string, object?> r, string k) =>
        V(r, k) is { } v ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : null;

    public static bool B(this Dictionary<string, object?> r, string k) => V(r, k) is bool b && b;

    public static Guid G(this Dictionary<string, object?> r, string k) => V(r, k) is Guid g ? g : Guid.Empty;

    public static Guid? GN(this Dictionary<string, object?> r, string k) => V(r, k) is Guid g ? g : null;

    public static string[] A(this Dictionary<string, object?> r, string k) => V(r, k) as string[] ?? Array.Empty<string>();

    public static DateTime T(this Dictionary<string, object?> r, string k) => V(r, k) is DateTime d ? d : DateTime.MinValue;

    public static DateTime? TN(this Dictionary<string, object?> r, string k) => V(r, k) is DateTime d ? d : null;
}
