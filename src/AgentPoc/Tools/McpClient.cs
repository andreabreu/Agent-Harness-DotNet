using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPoc.Core;

namespace AgentPoc.Tools;

/// <summary>Cliente MCP minimo via stdio (JSON-RPC 2.0, uma mensagem por linha).</summary>
public sealed class McpServerProcess : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId;

    public string Id { get; }

    private McpServerProcess(string id, Process proc)
    {
        Id = id;
        _proc = proc;
    }

    public static async Task<McpServerProcess> StartAsync(string id, string command, string[] args, JsonObject env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Env.RepoRoot,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in env) psi.Environment[kv.Key] = Env.Expand(kv.Value?.ToString() ?? "");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Nao foi possivel iniciar o servidor MCP '{id}'.");
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                    if (Env.Get("MCP_DEBUG") == "1") Log.Write(0, ConsoleColor.DarkGray, "mcp:" + id, line);
            }
            catch { /* processo encerrado */ }
        });

        var server = new McpServerProcess(id, proc);
        await server.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "AgentPoc", ["version"] = "1.0" }
        }, ct);
        await server.NotifyAsync("notifications/initialized");
        return server;
    }

    private async Task NotifyAsync(string method)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        await _proc.StandardInput.WriteLineAsync(msg.ToJsonString());
        await _proc.StandardInput.FlushAsync();
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var id = ++_nextId;
            var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) msg["params"] = parameters;
            await _proc.StandardInput.WriteLineAsync(msg.ToJsonString());
            await _proc.StandardInput.FlushAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                var line = await _proc.StandardOutput.ReadLineAsync(cts.Token)
                           ?? throw new IOException($"Servidor MCP '{Id}' encerrou a conexao.");
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch { continue; }
                if (node?["id"] is JsonValue idv && idv.TryGetValue<int>(out var rid) && rid == id)
                {
                    if (node["error"] is JsonNode err)
                        throw new InvalidOperationException($"MCP '{Id}' retornou erro: {err.ToJsonString()}");
                    return node["result"];
                }
                // notificacoes e requisicoes do servidor sao ignoradas nesta POC
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync();
        }
        catch { /* ignore */ }
        _proc.Dispose();
    }
}

/// <summary>Gerencia servidores MCP cadastrados em mcp_servers e importa suas tools para o registro.</summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly Db _db;
    private readonly Dictionary<string, McpServerProcess> _servers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public McpManager(Db db) => _db = db;

    private async Task<McpServerProcess> GetAsync(string serverId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_servers.TryGetValue(serverId, out var s)) return s;
            var row = await _db.QueryOneAsync("select id, command, args, env::text as env from mcp_servers where id = @id", ("id", serverId))
                      ?? throw new InvalidOperationException($"Servidor MCP '{serverId}' nao cadastrado.");
            var env = JsonNode.Parse(row.S("env")) as JsonObject ?? new JsonObject();
            var server = await McpServerProcess.StartAsync(serverId, row.S("command"), row.A("args"), env, ct);
            _servers[serverId] = server;
            return server;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ToolName(string server, string tool)
    {
        var name = Regex.Replace($"mcp_{server}_{tool}", "[^A-Za-z0-9_-]", "_");
        return name.Length > 64 ? name.Substring(0, 64) : name;
    }

    /// <summary>Conecta em cada servidor habilitado, chama tools/list e faz upsert em tools.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var row in await _db.QueryAsync("select id from mcp_servers where enabled order by id"))
        {
            var id = row.S("id");
            try
            {
                var server = await GetAsync(id, ct);
                var result = await server.RequestAsync("tools/list", new JsonObject(), ct);
                if (result?["tools"] is not JsonArray tools) continue;
                foreach (var t in tools)
                {
                    var toolName = HttpTool.ArgString(t?["name"]);
                    if (string.IsNullOrEmpty(toolName)) continue;
                    var schema = t?["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                    var desc = HttpTool.ArgString(t?["description"]);
                    await _db.ExecAsync(
                        @"insert into tools (name, description, kind, parameters, policy, config, keywords)
                          values (@n, @d, 'mcp', @p, 'approval', @c, @k)
                          on conflict (name) do update set description = excluded.description,
                              parameters = excluded.parameters, config = excluded.config, keywords = excluded.keywords",
                        ("n", ToolName(id, toolName)),
                        ("d", $"[MCP {id}] {(string.IsNullOrEmpty(desc) ? toolName : desc)}"),
                        ("p", schema),
                        ("c", new JsonObject { ["server"] = id, ["tool"] = toolName }),
                        ("k", $"mcp {id} {toolName.Replace('_', ' ')}"));
                    total++;
                }
                Log.Ok($"MCP '{id}': {tools.Count} ferramentas importadas para o registro.");
            }
            catch (Exception ex)
            {
                Log.Warn($"MCP '{id}' indisponivel: {ex.Message}");
            }
        }
        return total;
    }

    public async Task<string> CallAsync(ToolDef def, JsonObject args, CancellationToken ct)
    {
        var cfg = JsonNode.Parse(def.ConfigJson) as JsonObject ?? new JsonObject();
        var serverId = HttpTool.ArgString(cfg["server"]);
        var toolName = HttpTool.ArgString(cfg["tool"]);
        var server = await GetAsync(serverId, ct);
        var result = await server.RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = args.DeepClone()
        }, ct);

        var sb = new StringBuilder();
        if (result?["content"] is JsonArray content)
            foreach (var c in content)
                sb.AppendLine(HttpTool.ArgString(c?["type"]) == "text" ? HttpTool.ArgString(c?["text"]) : c?.ToJsonString());
        var isError = result?["isError"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return (isError ? "ERRO: " : "") + sb.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers.Values) await s.DisposeAsync();
        _servers.Clear();
    }
}
