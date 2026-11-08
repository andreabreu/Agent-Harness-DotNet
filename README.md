# AgentPoc: harness de agentes autonomos em .NET + Postgres

POC de console de um sistema agentico no estilo "Claude Code harness": o agente roda em loop
(modelo -> ferramentas -> modelo), descobre ferramentas em runtime, delega para outros agentes
(sincrono ou via fila), executa workflows deterministicos com checkpoint e retomada, respeita
politicas de aprovacao e budget, e grava tudo no Postgres. **Toda a configuracao vive no banco.**
A evidencia de cada execucao sai em Markdown + JSONL + JSON (sem HTML).

- .NET 8 (roda tambem com SDK 9/10), unica dependencia: `Npgsql`
- Modelos via API: Azure Foundry / Azure OpenAI, Anthropic, Foundry Local, Ollama, vLLM
- Postgres 16 via Docker

## Rodando no Mac

Pre-requisitos: .NET SDK 8+, Docker Desktop e `python3` (o agente programador executa scripts Python).

```bash
cd ~/Developer/test/_agents
cp .env.example .env              # preencha AZURE_FOUNDRY_ENDPOINT, _API_KEY e _DEPLOYMENT
docker compose up -d              # Postgres na porta 5433

cd src/AgentPoc
dotnet run -- setup               # cria schema e carrega agentes, tools, workflows e base de conhecimento
dotnet run -- check               # testa o tool calling do modelo configurado
dotnet run -- demo all            # roda os 5 cenarios e grava as evidencias
```

As evidencias ficam em `evidence/` (raiz do projeto). Comece por `evidence/INDEX.md`.
Cada pasta tem `timeline.md` (linha do tempo legivel, com checagens), `events.jsonl` (todos os
eventos crus), `summary.json` (metricas) e copias dos arquivos que os agentes produziram.

O endpoint do Foundry precisa ser o compativel com OpenAI v1, por exemplo
`https://<recurso>.openai.azure.com/openai/v1`, e o deployment precisa suportar tool calling
(gpt-4.1, gpt-4o, gpt-5, etc.). Se o modelo nao aceitar `max_completion_tokens` ou aceitar
`temperature`, ajuste a linha dele na tabela `models`.

### Sem chave de API (dublê de LLM)

Para validar a infraestrutura sem custo, ha um dublê compativel com a API do Foundry. Ele **nao e um
modelo**: decide as chamadas por regras fixas, so para exercitar o harness.

```bash
python3 tests/mock_llm.py &       # http://localhost:8765
export AZURE_FOUNDRY_ENDPOINT=http://localhost:8765/openai/v1 AZURE_FOUNDRY_API_KEY=teste AZURE_FOUNDRY_DEPLOYMENT=mock
cd src/AgentPoc && dotnet run -- demo all
```

## Chat

Conversa com um agente (por padrao o `assistant`) que decide sozinho o que fazer: responder direto,
pesquisar na web (`web_search` + `http_get`), consultar a base interna, planejar (`update_plan`),
raciocinar (`think`), descobrir ferramentas (`search_tools`), delegar a especialistas (`delegate_task`)
ou mandar trabalho para a fila (`create_task`). Ferramentas com politica `approval` pedem aprovacao.

```bash
cd src/AgentPoc
dotnet run -- chat                 # terminal: streaming, decisoes no prompt, Ctrl+C cancela a cadeia do turno
dotnet run -- serve                # web: http://localhost:5080 (API + tela + despachante)
dotnet run -- serve --no-dispatcher  # no so de API/tela; a retomada fica com outras instancias
dotnet run -- dispatcher           # instancia so de retomada (escala horizontal)
```

- **Multi-turno persistente**: cada mensagem fica em `conversation_messages`; cada turno e um run
  (`runs.conversation_id`) com a mesma auditoria em `run_events`. Da para retomar com
  `chat --conversation <id>` ou pela lista na tela.
- **Ferramentas descobertas continuam carregadas** nos turnos seguintes (`conversations.active_tools`).
- **Streaming**: a tela e o terminal acompanham a conversa pelos eventos gravados em `run_events` (todas as
  instancias enxergam o mesmo), entao o turno aparece andando mesmo quando quem o retoma e outra instancia.
  O texto token a token (OpenAI-compativel e Anthropic) chega quando o run executa no mesmo processo; vindo
  de outra instancia, o texto aparece por mensagem completa (`assistant_message`).
- **Contexto longo**: quando o historico passa de 50% da janela do modelo, os turnos antigos viram um
  resumo (`conversations.summary`); os 2 ultimos turnos ficam integrais. O custo do resumo vira um run.
- **Memoria**: a cada turno, memorias relevantes do agente entram no prompt; o agente salva fatos com `memory_save`.
- **Tarefas em segundo plano**: o chat sobe workers; quando uma tarefa criada na conversa termina, o
  resultado entra no proximo turno.
- **Human-in-the-loop** (detalhes abaixo): aprovar, editar argumentos, negar com motivo, responder perguntas
  do agente (`ask_human`), com espera duravel.
- **Cancelar**: botao Parar (web) ou Ctrl+C (terminal) cancela a cadeia inteira do turno: runs executando
  (em qualquer instancia, avisadas por `NOTIFY`) e runs suspensos viram `cancelled`, e os pedidos pendentes
  sao fechados, sem deixar o historico inconsistente.
- **Busca**: `SEARXNG_URL` (self-hosted, `docker compose --profile search up -d`), `BRAVE_API_KEY`, ou
  DuckDuckGo Lite sem configuracao.

### Human-in-the-loop

| Tipo | Como funciona |
|---|---|
| Aprovar | Ferramenta com politica `approval` (ou regra `approval_when`) para e mostra um card: Aprovar / Negar |
| Editar antes de aprovar | A pessoa corrige os argumentos no card; as regras `deny_when` sao reavaliadas sobre os argumentos editados |
| Negar com motivo | O motivo volta para o agente como resultado da ferramenta, e ele replaneja |
| Pergunta do agente | `ask_human(question, options)`: o agente pergunta no meio da tarefa e continua com a resposta |
| Regras por argumento | `tools.config`: `deny_when` bloqueia sem perguntar (ex.: `rm -rf`, `sudo`); `approval_when` exige aprovacao (ex.: `http_get` em URL interna) |

**Espera duravel em toda a cadeia, so com Postgres.** Nenhuma espera humana fica em memoria:

1. Quem pede uma pessoa (agente raiz ou subagente, via `ask_human` ou ferramenta `approval`) grava o pedido em
   `approvals` (com prazo em `expires_at`), fica `waiting_human` e libera o processo.
2. O pai de um subagente suspenso suspende junto: fica `waiting_child`, com `waiting_on_run_id` = filho e
   `pending_tool_call_id` = a chamada `delegate_task`. Isso sobe ate o agente raiz.
3. Gravar a decisao so marca o run que pediu como `ready` e avisa por `NOTIFY`. Nada e retomado dentro da
   requisicao HTTP nem do prompt do terminal.
4. O **despachante** (`ResumeDispatcher`, no `serve`, no `chat` ou sozinho com `dispatcher`) pega runs `ready`
   com `FOR UPDATE SKIP LOCKED` e os retoma do historico persistido (`conversation_messages` no agente raiz,
   `run_messages` nos demais). Acorda por `LISTEN/NOTIFY` e varre a cada 30 s. Varias instancias podem rodar o
   despachante: cada run e retomado por uma so.
5. Quando um filho termina, a saida dele vira o `tool_result` do `delegate_task` no pai, e o pai fica `ready`.
   Filho que falhou ou foi cancelado chega ao pai como erro da ferramenta.
6. Pedido sem resposta ate `expires_at` (padrao 24 h, `HITL_EXPIRES_HOURS`) e negado como `expired` pela
   varredura; o run e retomado e o agente replaneja.
7. Mensagem nova com a cadeia parada esperando alguem descarta a cadeia (`cancelled`, pedidos `expired`).

Budget e contadores continuam nos mesmos runs: na retomada, o budget usado inclui os tokens dos subagentes.
Na CLI (`chat`) a decisao e no terminal (`s`/`n`/`e` para editar, motivo, resposta); o comando `task`
continua com a aprovacao interativa direta, sem suspensao.

Nao precisa de Temporal para isso. O mesmo mecanismo (status + `ready` + despachante) serve depois para o
no `human` do workflow e para tarefas da fila que esperam uma pessoa.

API usada pela tela (sem autenticacao; escuta so em localhost):

| Metodo | Rota | O que faz |
|---|---|---|
| GET | `/api/agents` | agentes habilitados |
| GET/POST | `/api/conversations` | lista / cria (`{"agent":"assistant"}`) |
| GET | `/api/conversations/{id}` | mensagens, resumo, ferramentas ativas, tokens, custo, aprovacoes pendentes |
| GET | `/api/conversations/{id}/stream?after=<id>` | Server-Sent Events da conversa (eventos gravados + texto ao vivo; reconecta por `Last-Event-ID`) |
| POST | `/api/conversations/{id}/messages` | envia `{"text":"..."}`; responde 202 e o turno roda em segundo plano |
| POST | `/api/conversations/{id}/cancel` | cancela a cadeia do turno (em todas as instancias) |
| POST | `/api/approvals/{id}` | decide `{"approved":true,"reason":"...","args":{...}}` ou responde `{"answer":"..."}`; so grava |

## Comandos

| Comando | O que faz |
|---|---|
| `chat [--agent assistant] [--conversation <id>] [--verbose] [--no-worker]` | chat no terminal |
| `serve [--port 5080] [--no-dispatcher]` | chat web + API (+ despachante, a menos de `--no-dispatcher`) |
| `dispatcher [--concurrency N]` | so retoma runs prontos (decisao humana, prazo vencido, filho que terminou) |
| `setup [--reset]` | cria/atualiza o schema e o seed (idempotente; `--reset` apaga tudo) |
| `check` | valida o `.env` e testa tool calling em cada modelo usado pelos agentes |
| `agents` / `tools` | mostra a configuracao lida do banco |
| `task <agente> "<texto>"` | roda um agente; pede aprovacao no terminal para ferramentas `approval` |
| `workflow <id> "<entrada>" [--crash-after <no>]` | roda um workflow (opcionalmente simulando queda) |
| `workflow --resume <workflow_run_id>` | retoma um workflow a partir dos checkpoints |
| `worker [--concurrency N] [--once]` | processa a fila de tarefas assincronas |
| `sync-mcp` | importa as tools dos servidores MCP habilitados |
| `demo [cenario\|all]` | roda os cenarios de demonstracao com checagens automaticas |
| `report <run_id\|workflow_run_id>` | gera evidencia de qualquer execucao ja gravada |

Opcoes: `--approval auto|deny|ask`, `--budget <tokens>`.

## Os cenarios da demo

| Cenario | O que prova |
|---|---|
| `autonomia` | Orquestrador recebe so o objetivo, lista a equipe, delega; especialistas descobrem ferramentas com `search_tools`, consultam a base interna, chamam uma API HTTP cadastrada so no banco, escrevem e executam codigo, e o resultado vira `relatorio_final.md` |
| `workflow` | Workflow em JSON no banco: no de ferramenta + nos de agente, dois ramos em paralelo, join, checkpoint por no |
| `fila` | Orquestrador cria tarefas na fila e nao espera; 3 workers concorrentes pegam com `FOR UPDATE SKIP LOCKED` |
| `governanca` | `run_shell` (approval) negado, `delete_file` (deny) bloqueado, e um run com budget pequeno interrompido |
| `retomada` | Queda simulada no meio do workflow; a retomada reaproveita os nos concluidos sem reexecutar |
| `hitl` | Pergunta respondida por outra instancia (restart), aprovar com edicao, negar com motivo, regra deny_when, regra approval_when, subagente suspenso com o pai, pendencia descartada |
| `hitl_sub` | Orquestrador delega a um subagente que chama `ask_human`: filho `waiting_human`, pai `waiting_child`; outra instancia responde; o despachante retoma de baixo para cima e o pai termina com a resposta do filho |
| `hitl_concorrencia` | 4 cadeias suspensas, duas instancias com despachante: cada run retomado uma vez, sem eventos duplicados |
| `hitl_expira` | Pedido vencido: negado como `expired`, o subagente replaneja e o pai termina |
| `hitl_cancela` | Cancelar o raiz com subagente suspenso: toda a cadeia `cancelled`, pedido fechado, nada retomado |
| `chat` | Conversa de 5 turnos: pesquisa na web, plano + delegacao, resposta pelo historico, aprovacao negada, ferramentas persistidas, streaming e compactacao em resumo |

## Como funciona

```
src/AgentPoc/
  Harness/AgentRunner.cs    o loop do agente, budget, compactacao de contexto, eventos, suspensao e retomada
  Harness/ToolExecutor.cs   politica auto/approval/deny (+ regras por argumento) + execucao + auditoria
  Harness/RunChain.cs       transicoes duraveis entre runs: ready, entrega filho -> pai, expiracao, cancelamento
  Harness/ResumeDispatcher.cs  retoma runs ready (SKIP LOCKED, LISTEN/NOTIFY, varredura)
  Human/                    pedidos humanos (approvals) e o gate duravel
  Tools/BuiltinTools.cs     search_tools, delegate_task, create_task, memoria, KB, arquivos, shell, web
  Tools/HttpTool.cs         executor generico de ferramentas HTTP definidas so no banco
  Tools/McpClient.cs        cliente MCP via stdio (tools/list e tools/call)
  Llm/                      clientes OpenAI-compativel (Foundry, Foundry Local, Ollama) e Anthropic, com streaming
  Chat/                     conversa: persistencia, turno, compactacao, CLI
  Web/                      API REST + SSE, aprovacao pela tela; pagina em wwwroot/index.html
  Tools/WebSearch.cs        busca na web (SearXNG, Brave ou DuckDuckGo Lite)
  Workflows/                motor de grafo com paralelismo, checkpoint e retomada
  Queue/Worker.cs           fila com claim atomico, heartbeat e retentativa
  Evidence/                 relatorios de evidencia a partir do banco
sql/schema.sql, seed.sql    tudo que define comportamento
```

- **Loop**: o harness carrega o agente do banco, chama o modelo com as ferramentas carregadas,
  executa as tool calls, devolve os resultados e repete ate o modelo responder sem ferramentas,
  ou ate bater `max_steps` ou o budget. Cada passo vira um evento em `run_events`.
- **Descoberta**: cada agente comeca com poucas ferramentas (`initial_tools`). `search_tools` faz
  busca textual no registro e carrega o que achar, dentro do escopo `allowed_tools` do agente.
- **Delegacao**: e so uma ferramenta. `delegate_task` cria um run filho com contexto limpo e budget
  que e uma fatia do saldo do pai (consumo do filho desconta do pai); profundidade maxima 3.
  `create_task` faz a versao assincrona, via fila.
- **Politicas**: cada ferramenta tem `policy` = `auto`, `approval` ou `deny`, refinada por regras por
  argumento (`deny_when`, `approval_when`). Toda decisao fica auditada.
- **Suspensao duravel**: um run pode parar sem processo preso (`waiting_human`, `waiting_child`), com todo o
  historico no banco. Qualquer instancia com o despachante retoma o mesmo run quando ele fica `ready`.
  Estados: `running` -> `waiting_human` | `waiting_child` -> `ready` -> `running` -> `completed`/... ;
  suspensos podem ir direto para `cancelled`.

## Configurando pelo banco (sem recompilar)

```sql
-- trocar o modelo de um agente (ex.: coder no Claude, researcher num modelo local)
update agents set model_id = 'claude_default' where id = 'coder';
update agents set model_id = 'local_default'  where id = 'researcher';

-- nova ferramenta HTTP, so com SQL
insert into tools (name, description, kind, parameters, policy, config, keywords) values
 ('cotacao_moeda', 'Cotacao atual de uma moeda contra o real.', 'http',
  '{"type":"object","properties":{"moeda":{"type":"string"}},"required":["moeda"]}', 'auto',
  '{"method":"GET","url":"https://economia.awesomeapi.com.br/json/last/{moeda}-BRL"}', 'cambio dolar euro moeda');
update agents set allowed_tools = allowed_tools || '{cotacao_moeda}' where id = 'researcher';

-- novo agente
insert into agents (id, description, model_id, system_prompt, initial_tools, allowed_tools)
values ('analista', 'Analista financeiro', 'foundry_default', 'Voce e um analista financeiro...',
        '{search_tools}', '{cotacao_moeda,kb_*}');

-- servidor MCP (exemplo sem Node incluso em tests/)
insert into mcp_servers (id, command, args, enabled) values ('demo', 'python3', '{tests/mcp_echo_server.py}', true);
-- depois: dotnet run -- sync-mcp

-- deixar o shell livre (autonomia total para essa ferramenta)
update tools set policy = 'auto' where name = 'run_shell';
```

## O que foi testado e o que nao foi

Testado em Linux com .NET 8: o projeto compila sem erros nem avisos, e os 5 cenarios passaram
33/33 checagens usando o dublê de LLM e um Postgres 16 real. Tambem foram exercitados: `check`,
`task` com aprovacao interativa, queda e retomada pela CLI, `report`, `worker`, MCP via stdio,
o cliente Anthropic (formato `tool_use`/`tool_result`) e os caminhos de erro (chave ausente, banco fora).

Depois, no Mac (macOS, .NET 8, Npgsql oficial, Postgres 16 em Docker), com o chat: os 6 cenarios
passaram 52/52 checagens com o dublê (7 cenarios); `chat` no terminal e `serve` no navegador (streaming, plano,
delegacao, aprovar/negar pela tela, recarregar a pagina com aprovacao pendente, botao Parar); streaming
nos formatos OpenAI e Anthropic; `web_search` com busca real via SearXNG e DuckDuckGo Lite; `setup` sem
`--reset` sobre banco existente; HITL na tela (responder pergunta depois de derrubar e subir o servidor,
editar argumentos e aprovar). Testes unitarios: `dotnet test tests/AgentPoc.Tests` (28 testes).

Com a suspensao duravel em toda a cadeia: os 11 cenarios passaram 69/69 checagens; `hitl_concorrencia` 5
rodadas seguidas sem falha; `tests/hitl_restart.sh` (processo encerrado com `kill -9` com a pergunta do
subagente pendente, processo novo responde e termina a cadeia); tela servida por um processo sem
despachante mostrando a retomada feita por outro processo (`dispatcher`); `chat` no terminal com decisao no
prompt. Testes unitarios: 52.

Nao testado: contra o Azure Foundry real (inclusive `stream_options` e `reasoning_effort` no deployment)
e a Brave Search API. O primeiro `dotnet run -- check` com o `.env` preenchido confirma o modelo.

## Limitacoes conhecidas da POC

- `run_shell` roda no workspace do host, nao num sandbox. Em producao, execute em container efemero.
- Busca de ferramentas e da base de conhecimento e textual (tsvector). Para semantica, use pgvector.
- O budget e checado entre chamadas ao modelo, entao um run pode passar do limite em uma chamada.
- Na CLI a aprovacao e por terminal ou pelo modo global (`APPROVAL_MODE`); no worker o modo `ask` pausa tudo.
- Sem multi-tenant e sem autenticacao: o `serve` escuta so em localhost.
- Espera humana e duravel, mas a EXECUCAO nao: se uma instancia cair enquanto um run esta `running` (gerando
  com o modelo ou rodando ferramenta), ele fica `running` sem dono. Falta lease/heartbeat por run para outra
  instancia reivindica-lo; hoje a mensagem nova na conversa responde "ocupado" ate alguem marcar o run.
- Ferramentas reexecutadas na retomada sao so as que ainda nao tinham resultado; nao ha idempotencia para uma
  ferramenta que chegou a executar mas o processo caiu antes de gravar o resultado.
- Ha uma janela de milissegundos na entrega filho -> pai (resultado gravado, pai ainda nao `ready`); uma queda
  exatamente ali deixa o pai em `waiting_child` sem filho; a varredura nao recupera esse caso.
- Texto token a token so aparece quando o run executa no mesmo processo que serve a tela; de outra instancia,
  por mensagem completa. O stream da tela le o banco a cada 250 ms.
- Tarefas da fila (`worker`) e nos de workflow ainda nao esperam pessoa: usam o modo de aprovacao da CLI
  (`APPROVAL_MODE`, padrao `auto` no `serve`).
- Sem autenticacao, nao ha registro de QUEM decidiu nem aprovador por papel/notificacao (Teams, e-mail).
- DuckDuckGo Lite e raspagem de HTML e pode limitar por excesso de uso; em producao use SearXNG ou uma API.
- O MCP suporta so stdio e so tools (sem resources, prompts nem HTTP/SSE).
