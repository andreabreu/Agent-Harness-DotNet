using System.Text;
using AgentPoc.Core;
using AgentPoc.Llm;

namespace AgentPoc.Chat;

/// <summary>
/// Quando o historico ativo passa de uma fracao da janela de contexto do modelo, resume os turnos antigos
/// e guarda o resumo na conversa. Os turnos mais recentes continuam integrais.
/// </summary>
public sealed class ChatCompactor
{
    private const double ContextShareBeforeSummary = 0.5;
    private const int CharsPerToken = 4;
    private const int TurnsKeptIntact = 2;
    private const int ToolOutputCharsInTranscript = 1500;
    private const string SummarizerAgentId = "system:summarizer";

    private const string SummaryInstructions =
        "Voce resume conversas entre um usuario e um assistente para que o assistente continue o trabalho sem o historico completo. " +
        "Mantenha fatos, numeros, decisoes, nomes de arquivos, URLs consultadas, pendencias e preferencias do usuario. " +
        "Descarte cortesias e detalhes de ferramentas que nao importam mais. Escreva em portugues, em topicos, com no maximo 400 palavras.";

    private readonly Db _db;
    private readonly ConversationStore _store;
    private readonly ConfigRepo _config;

    public ChatCompactor(Db db, ConversationStore store, ConfigRepo config)
    {
        _db = db;
        _store = store;
        _config = config;
    }

    /// <summary>Compacta se necessario e devolve a conversa atualizada.</summary>
    public async Task<Conversation> CompactIfNeededAsync(Conversation conv, CancellationToken ct)
    {
        var agent = await _config.GetAgentAsync(conv.AgentId)
                    ?? throw new InvalidOperationException($"Agente '{conv.AgentId}' nao existe ou esta desabilitado.");
        var model = await _config.GetModelAsync(agent.ModelId);
        var active = await _store.LoadMessagesAsync(conv.Id, conv.SummarizedUpto);
        var limitChars = (long)(model.ContextWindow * ContextShareBeforeSummary * CharsPerToken);
        if (ChatHistory.CharCount(active.Select(m => m.Message)) <= limitChars) return conv;

        var cutoff = ChatHistory.FindSummaryCutoff(active.Select(m => (m.Seq, m.Message.Role)).ToList(), TurnsKeptIntact);
        if (cutoff == null) return conv;

        var transcript = Transcript(active.Where(m => m.Seq <= cutoff).Select(m => m.Message));
        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(conv.Summary)) prompt.AppendLine("Resumo anterior:").AppendLine(conv.Summary).AppendLine();
        prompt.AppendLine("Trecho da conversa a incorporar no resumo:").AppendLine(transcript);

        var resp = await LlmFactory.For(model).CompleteAsync(model,
            new LlmRequest(SummaryInstructions, new[] { ChatMessage.User(prompt.ToString()) }, Array.Empty<ToolSpec>(), null), ct);
        var summary = string.IsNullOrWhiteSpace(resp.Content) ? conv.Summary ?? "" : resp.Content.Trim();
        await RecordCostAsync(conv.Id, model, resp);
        await _store.SaveSummaryAsync(conv.Id, summary, cutoff.Value);
        Log.Info($"Conversa {Text.Short(conv.Id)} compactada: mensagens ate #{cutoff} viraram resumo.");
        return conv with { Summary = summary, SummarizedUpto = cutoff.Value };
    }

    private static string Transcript(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "user":
                    sb.AppendLine($"USUARIO: {m.Content}");
                    break;
                case "assistant":
                    if (!string.IsNullOrWhiteSpace(m.Content)) sb.AppendLine($"ASSISTENTE: {m.Content}");
                    foreach (var c in m.ToolCalls ?? new List<ToolCall>()) sb.AppendLine($"ASSISTENTE chamou {c.Name}({Text.Trunc(c.ArgumentsJson, 300)})");
                    break;
                case "tool":
                    sb.AppendLine($"RESULTADO {m.ToolName}: {Text.Trunc(m.Content, ToolOutputCharsInTranscript)}");
                    break;
            }
        }
        return sb.ToString();
    }

    private Task RecordCostAsync(Guid conversationId, ModelCfg model, LlmResponse resp)
    {
        var cost = (resp.InputTokens * model.CostInPerMTok + resp.OutputTokens * model.CostOutPerMTok) / 1_000_000m;
        var runId = Guid.NewGuid();
        return _db.ExecAsync(
            @"insert into runs (id, root_run_id, conversation_id, agent_id, model_id, input, output, status, steps, tokens_in, tokens_out, cost_usd, finished_at)
              values (@id, @id, @c, @a, @m, 'resumo da conversa', @o, 'completed', 1, @ti, @to, @cost, now())",
            ("id", runId), ("c", conversationId), ("a", SummarizerAgentId), ("m", model.Id), ("o", resp.Content ?? ""),
            ("ti", resp.InputTokens), ("to", resp.OutputTokens), ("cost", cost));
    }
}
