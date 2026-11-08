using AgentPoc.Llm;

namespace AgentPoc.Chat;

/// <summary>Regras puras sobre o historico da conversa (sem banco, testaveis).</summary>
public static class ChatHistory
{
    public const string InterruptedToolResult =
        "INTERROMPIDO: esta ferramenta nao chegou a executar (o processo parou ou a pessoa seguiu a conversa sem decidir).";

    /// <summary>
    /// Garante que toda tool call tenha um resultado logo depois dela. Os provedores rejeitam historico com
    /// tool calls sem resposta, o que acontece se o processo cair no meio de um turno.
    /// Com <paramref name="keepTrailingPending"/>, as tool calls sem resultado da ultima mensagem continuam
    /// pendentes: e o caso da retomada apos uma decisao humana, que vai executa-las.
    /// </summary>
    public static List<ChatMessage> Repair(IReadOnlyList<ChatMessage> messages, bool keepTrailingPending = false)
    {
        var repaired = new List<ChatMessage>(messages.Count);
        var pending = new List<ToolCall>();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                var match = pending.FindIndex(c => c.Id == m.ToolCallId);
                if (match < 0) continue;
                pending.RemoveAt(match);
                repaired.Add(m);
                continue;
            }
            CloseOrphans(repaired, pending);
            repaired.Add(m);
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } calls) pending.AddRange(calls);
        }
        if (!keepTrailingPending) CloseOrphans(repaired, pending);
        return repaired;
    }

    private static void CloseOrphans(List<ChatMessage> target, List<ToolCall> pending)
    {
        foreach (var c in pending) target.Add(ChatMessage.Tool(c.Id, c.Name, InterruptedToolResult));
        pending.Clear();
    }

    /// <summary>
    /// Ultimo seq que pode ir para o resumo mantendo os <paramref name="keepTurns"/> turnos mais recentes intactos.
    /// O corte cai sempre antes de uma mensagem do usuario, para nunca separar uma tool call do seu resultado.
    /// Retorna null quando nao ha o que compactar.
    /// </summary>
    public static int? FindSummaryCutoff(IReadOnlyList<(int Seq, string Role)> messages, int keepTurns)
    {
        var userSeqs = messages.Where(m => m.Role == "user").Select(m => m.Seq).ToList();
        if (userSeqs.Count <= keepTurns) return null;
        var firstKept = userSeqs[userSeqs.Count - keepTurns];
        var cutoff = messages.Where(m => m.Seq < firstKept).Select(m => (int?)m.Seq).Max();
        return cutoff;
    }

    public static long CharCount(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => (long)(m.Content?.Length ?? 0) + (m.ToolCalls?.Sum(c => c.ArgumentsJson.Length + c.Name.Length) ?? 0));
}
