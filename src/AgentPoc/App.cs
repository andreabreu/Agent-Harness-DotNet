using AgentPoc.Chat;
using AgentPoc.Core;
using AgentPoc.Evidence;
using AgentPoc.Harness;
using AgentPoc.Human;
using AgentPoc.Queue;
using AgentPoc.Tools;
using AgentPoc.Workflows;

namespace AgentPoc;

/// <summary>Composicao das pecas (sem container de DI para manter a POC legivel).</summary>
public sealed class App : IAsyncDisposable
{
    public Db Db { get; }
    public ConfigRepo Config { get; }
    public ToolRegistry Registry { get; }
    public McpManager Mcp { get; }
    public EventLog Events { get; }
    public BuiltinTools Builtins { get; }
    public ToolExecutor Executor { get; }
    public AgentRunner Runner { get; }
    public WorkflowEngine Workflows { get; }
    public Worker Worker { get; }
    public EvidenceWriter Evidence { get; }
    public MemoryStore Memory { get; }
    public ChatService Chat { get; }

    /// <summary>Human-in-the-loop do chat: espera duravel no agente raiz, em memoria nos subagentes.</summary>
    public ChatHumanGate HumanGate { get; }

    public string ConnectionString { get; }

    /// <summary>Quanto um subagente espera em memoria por uma decisao antes de seguir como expirada.</summary>
    private static readonly TimeSpan SubagentHumanTimeout = TimeSpan.FromMinutes(10);

    public App(string connectionString)
    {
        ConnectionString = connectionString;
        Db = new Db(connectionString);
        Config = new ConfigRepo(Db);
        Registry = new ToolRegistry(Db);
        Mcp = new McpManager(Db);
        Events = new EventLog(Db);
        Memory = new MemoryStore(Db);
        Builtins = new BuiltinTools(Db, Registry, Config, Events, Memory);
        Executor = new ToolExecutor(Builtins, Mcp, Events);
        Runner = new AgentRunner(Db, Config, Registry, Executor, Events);
        Builtins.Runner = Runner;
        Workflows = new WorkflowEngine(Db, Runner);
        Worker = new Worker(Db, Runner);
        Evidence = new EvidenceWriter(Db);
        var conversations = new ConversationStore(Db);
        var human = new HumanInputStore(Db);
        var waiters = new InMemoryWaiters();
        HumanGate = new ChatHumanGate(human, Events, waiters, SubagentHumanTimeout);
        Chat = new ChatService(conversations, new ChatCompactor(Db, conversations, Config), Runner, Config, Memory, human, waiters);
    }

    public async ValueTask DisposeAsync()
    {
        await Mcp.DisposeAsync();
        await Db.DisposeAsync();
    }
}
