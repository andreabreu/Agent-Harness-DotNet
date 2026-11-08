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
dotnet run -- chat                 # terminal: streaming, aprovacao no prompt, Ctrl+C cancela o turno
dotnet run -- serve                # web: http://localhost:5080
```

- **Multi-turno persistente**: cada mensagem fica em `conversation_messages`; cada turno e um run
  (`runs.conversation_id`) com a mesma auditoria em `run_events`. Da para retomar com
  `chat --conversation <id>` ou pela lista na tela.
- **Ferramentas descobertas continuam carregadas** nos turnos seguintes (`conversations.active_tools`).
- **Streaming** do texto (OpenAI-compativel e Anthropic) e dos eventos: ferramentas, delegacoes com o
  subagente identificado, plano, raciocinio, aprovacoes, tokens e custo ao vivo.
- **Contexto longo**: quando o historico passa de 50% da janela do modelo, os turnos antigos viram um
  resumo (`conversations.summary`); os 2 ultimos turnos ficam integrais. O custo do resumo vira um run.
- **Memoria**: a cada turno, memorias relevantes do agente entram no prompt; o agente salva fatos com `memory_save`.
- **Tarefas em segundo plano**: o chat sobe workers; quando uma tarefa criada na conversa termina, o
  resultado entra no proximo turno.
- **Human-in-the-loop** (detalhes abaixo): aprovar, editar argumentos, negar com motivo, responder perguntas
  do agente (`ask_human`), com espera duravel.
- **Cancelar**: botao Parar (web) ou Ctrl+C (terminal) encerra o turno e os subagentes como `cancelled`,
  sem deixar o historico inconsistente.
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

**Espera duravel**: no agente raiz da conversa, o pedido nao segura processo. O run fica `waiting_human`, o
pedido fica em `approvals` e, quando a pessoa decide (minutos ou dias depois, mesmo apos restart do servidor),
o MESMO run e retomado do historico persistido: a tool call pendente e reexecutada e encontra a decisao gravada.
Se a pessoa mandar outra mensagem sem decidir, o pedido expira e o turno suspenso e encerrado.
Subagentes esperam em memoria (ate 10 min), porque o historico deles nao e persistido; um restart nesse
momento perde a espera. Na CLI (`chat`, `task`) a decisao e no terminal (`s`/`n`/`e` para editar, motivo, resposta).

Nao precisa de Temporal para isso: o chat ja persiste tudo no Postgres. O Temporal passa a valer a pena para
esperas longas dentro de workflows, tarefas da fila e subagentes, e para timers/escalonamento em volume.

API usada pela tela (sem autenticacao; escuta so em localhost):

| Metodo | Rota | O que faz |
|---|---|---|
| GET | `/api/agents` | agentes habilitados |
| GET/POST | `/api/conversations` | lista / cria (`{"agent":"assistant"}`) |
| GET | `/api/conversations/{id}` | mensagens, resumo, ferramentas ativas, tokens, custo, aprovacoes pendentes |
| POST | `/api/conversations/{id}/messages` | envia `{"text":"..."}` e recebe o turno em Server-Sent Events |
| POST | `/api/conversations/{id}/cancel` | cancela o turno em andamento |
| POST | `/api/approvals/{id}` | decide `{"approved":true,"reason":"...","args":{...}}` ou responde `{"answer":"..."}`; se o turno estava suspenso, responde com o SSE da continuacao |
| POST | `/api/conversations/{id}/resume` | continua (SSE) um turno suspenso cuja decisao ja foi gravada |

## Comandos

| Comando | O que faz |
|---|---|
| `chat [--agent assistant] [--conversation <id>] [--verbose] [--no-worker]` | chat no terminal |
| `serve [--port 5080]` | chat web + API |
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
| `hitl` | Pergunta com retomada por outra instancia (restart), aprovar com edicao, negar com motivo, regra deny_when, regra approval_when, subagente esperando em memoria, pendencia descartada |
| `chat` | Conversa de 5 turnos: pesquisa na web, plano + delegacao, resposta pelo historico, aprovacao negada, ferramentas persistidas, streaming e compactacao em resumo |

## Como funciona

```
src/AgentPoc/
  Harness/AgentRunner.cs    o loop do agente, budget, compactacao de contexto, eventos
  Harness/ToolExecutor.cs   politica auto/approval/deny + execucao + auditoria
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
- **Politicas**: cada ferramenta tem `policy` = `auto`, `approval` ou `deny`. Toda decisao fica auditada.

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

Nao testado: contra o Azure Foundry real (inclusive `stream_options` e `reasoning_effort` no deployment)
e a Brave Search API. O primeiro `dotnet run -- check` com o `.env` preenchido confirma o modelo.

## Limitacoes conhecidas da POC

- `run_shell` roda no workspace do host, nao num sandbox. Em producao, execute em container efemero.
- Busca de ferramentas e da base de conhecimento e textual (tsvector). Para semantica, use pgvector.
- O budget e checado entre chamadas ao modelo, entao um run pode passar do limite em uma chamada.
- Na CLI a aprovacao e por terminal ou pelo modo global (`APPROVAL_MODE`); no worker o modo `ask` pausa tudo.
- Sem multi-tenant e sem autenticacao: o `serve` escuta so em localhost.
- Turno do chat web vive no processo: se o servidor cair no meio, as mensagens ja gravadas ficam, as
  tool calls sem resultado sao marcadas como interrompidas no proximo turno, mas o turno nao e retomado.
- Aprovacao de subagente na web espera em memoria ate 10 minutos; depois segue como expirada.
- Sem autenticacao, nao ha registro de QUEM decidiu nem aprovador por papel/notificacao (Teams, e-mail).
- DuckDuckGo Lite e raspagem de HTML e pode limitar por excesso de uso; em producao use SearXNG ou uma API.
- O MCP suporta so stdio e so tools (sem resources, prompts nem HTTP/SSE).
