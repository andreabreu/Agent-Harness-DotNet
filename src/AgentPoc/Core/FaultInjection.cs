namespace AgentPoc.Core;

/// <summary>O processo "caiu" num ponto de falha injetado: ninguem trata, o estado fica como estava no banco.</summary>
public sealed class InjectedFaultException : Exception
{
    public InjectedFaultException(string point) : base($"Queda simulada no ponto '{point}' (AGENTPOC_FAULT).") { }
}

/// <summary>
/// Pontos de falha para testes de durabilidade, ligados por AGENTPOC_FAULT=&lt;ponto&gt;. Disparam uma unica vez
/// por processo (a variavel e limpa ao disparar). Sem a variavel, nao fazem nada.
/// </summary>
public static class FaultInjection
{
    public const string Variable = "AGENTPOC_FAULT";
    public const string AfterChildFinished = "after_child_finished";

    public static void Point(string name)
    {
        if (Environment.GetEnvironmentVariable(Variable) != name) return;
        Environment.SetEnvironmentVariable(Variable, null);
        throw new InjectedFaultException(name);
    }
}
