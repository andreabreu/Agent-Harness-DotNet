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

    /// <summary>Human-in-the-loop duravel: o run suspende (waiting_human) e o despachante retoma depois.</summary>
    public DurableHumanGate HumanGate { get; }

    public RunChain Chain { get; }
    public ResumeDispatcher Dispatcher { get; }
    public ConversationFeed Feed { get; }
    public LiveHub Live { get; }

    public string ConnectionString { get; }

    /// <summary>Prazo para uma pessoa responder um pedido (HITL_EXPIRES_HOURS); vencido, o pedido e negado como expired.</summary>
    private static TimeSpan HumanRequestExpiry =>
        TimeSpan.FromHours(double.TryParse(Env.Get("HITL_EXPIRES_HOURS"), System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 24);

    public App(string connectionString)
    {
        ConnectionString = connectionString;
        Db = new Db(connectionString);
        Config = new ConfigRepo(Db);
        Registry = new ToolRegistry(Db);
        Mcp = new McpManager(Db);
        Events = new EventLog(Db);
        Memory = new MemoryStore(Db);
        var runMessages = new RunMessageStore(Db);
        var conversations = new ConversationStore(Db);
        Chain = new RunChain(Db, Events, runMessages, conversations);
        Builtins = new BuiltinTools(Db, Registry, Config, Events, Memory);
        Executor = new ToolExecutor(Builtins, Mcp, Events);
        Runner = new AgentRunner(Db, Config, Registry, Executor, Events, runMessages, Chain);
        Builtins.Runner = Runner;
        Workflows = new WorkflowEngine(Db, Runner);
        Worker = new Worker(Db, Runner);
        Evidence = new EvidenceWriter(Db);
        var human = new HumanInputStore(Db);
        HumanGate = new DurableHumanGate(human, Events, HumanRequestExpiry);
        var executions = new RunExecutions();
        Live = new LiveHub();
        Feed = new ConversationFeed(Db);
        Chat = new ChatService(conversations, new ChatCompactor(Db, conversations, Config), Runner, Config, Memory, human, Chain, executions);
        Dispatcher = new ResumeDispatcher(Db, Runner, Chain, conversations, executions, HumanGate,
            conversationId => conversationId == null ? null : new LiveObserver(Live));
    }

    public async ValueTask DisposeAsync()
    {
        await Mcp.DisposeAsync();
        await Db.DisposeAsync();
    }
}
