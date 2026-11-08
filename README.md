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
| Uma decisao, uma execucao | A decisao vale so para a mesma ferramenta com os mesmos argumentos e e marcada como usada (`approvals.consumed_at`) ao ser aplicada; uma chamada identica depois (ids como `call_0` se repetem em modelos locais) gera um pedido novo |

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
| `workflow <id> "<entrada>" [--crash-after <no>] [--approval deny\|auto\|ask]` | roda um workflow (opcionalmente simulando queda); sem modo informado, nega ferramentas de aprovacao |
| `workflow --resume <workflow_run_id> [--approval ...]` | retoma um workflow a partir dos checkpoints, com o modo de aprovacao gravado na execucao |
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
| `durabilidade_entrega` | Queda injetada (`AGENTPOC_FAULT`) dentro da transacao que conclui o filho e acorda o pai: nada fica pela metade, o lease vence, o filho e recuperado sem chamar o modelo de novo e o pai termina com um unico `tool_result`; pai preso forjado no banco e corrigido pela reconciliacao |
| `workflow_aprovacao` | Workflow sem modo informado nega `run_shell` (no de ferramenta e subagente), modo `auto` explicito executa, `tools.allow` libera, `tools.block` bloqueia ferramenta `auto`, `deny_when` vence o `allow`, tarefa da fila criada no workflow herda o padrao no worker, regra com ferramenta inexistente impede o inicio; cada decisao grava a origem |
| `workflow_desvio` | Triagem: classificador com `output_schema` (nova tentativa quando erra o formato), desvio com 3 casos e `default`, ramos pulados, join `all_active`, join `any`, `transform` com tipos preservados, `end` com falha, entrada fora do `input_schema` e definicao invalida rejeitadas, queda depois do desvio retomada sem reclassificar |
| `chat` | Conversa de 5 turnos: pesquisa na web, plano + delegacao, resposta pelo historico, aprovacao negada, ferramentas persistidas, streaming e compactacao em resumo |

## Como funciona

```
src/AgentPoc/
  Harness/AgentRunner.cs    o loop do agente, budget, compactacao de contexto, eventos, suspensao e retomada
  Harness/ToolExecutor.cs   politica auto/approval/deny (+ regras por argumento) + execucao + auditoria
  Harness/RunChain.cs       transicoes duraveis entre runs: fim + entrega filho -> pai numa transacao, ready,
                            orfaos por lease, reconciliacao, expiracao, cancelamento
  Harness/ResumeDispatcher.cs  retoma runs ready (SKIP LOCKED, LISTEN/NOTIFY, varredura)
  Harness/RunLease.cs       lease por run + heartbeat
  Harness/ToolRecovery.cs   idempotencia de ferramenta na retomada (repetir / perguntar)
  Human/                    pedidos humanos (approvals) e o gate duravel
  Tools/BuiltinTools.cs     search_tools, delegate_task, create_task, memoria, KB, arquivos, shell, web
  Tools/HttpTool.cs         executor generico de ferramentas HTTP definidas so no banco
  Tools/McpClient.cs        cliente MCP via stdio (tools/list e tools/call)
  Llm/                      clientes OpenAI-compativel (Foundry, Foundry Local, Ollama) e Anthropic, com streaming
  Chat/                     conversa: persistencia, turno, compactacao, CLI
  Web/                      API REST + SSE, aprovacao pela tela; pagina em wwwroot/index.html
  Tools/WebSearch.cs        busca na web (SearXNG, Brave ou DuckDuckGo Lite)
  Workflows/                motor de grafo (definicao validada, escalonador, condicoes, schema, valores)
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
  argumento (`deny_when`, `approval_when`). Toda decisao fica auditada, com a origem no campo `source` do evento
  `policy` (`tool_policy`, `deny_when`, `approval_when`, `workflow_block`, `workflow_allow`, `human`,
  `mode_default`, `mode_explicit`).
- **Aprovacao em workflow**: cada execucao tem o proprio modo (`--approval` ou `APPROVAL_MODE`; sem nenhum dos
  dois, `deny`, porque nao ha quem responda). O modo fica em `workflow_runs.approval_mode` e vale para nos de
  agente, seus subagentes e nos de ferramenta; o `--resume` usa o gravado. A definicao pode ter
  `"tools": {"allow": [...], "block": [...]}` (curingas como em `allowed_tools`). Precedencia: `deny` global >
  `deny_when` > `block` > `approval_when` > `allow` > politica da ferramenta; `allow` so dispensa a politica
  `approval`. Padrao sem ferramenta correspondente impede o inicio. Tarefa criada na fila por um agente do
  workflow (`create_task`) guarda `tasks.workflow_run_id`, e o worker aplica o modo e as regras daquela execucao.
- **Nos de workflow**: `agent`, `tool`, `condition` (desvio), `transform` (monta JSON sem modelo) e `end` (termina
  com `completed` ou `failed`). A definicao e validada antes de iniciar e todos os erros voltam juntos
  (`rotear.cases[0].next: destino 'juridco' nao existe`).
  - *Desvio*: `cases` em ordem (o primeiro verdadeiro vence) e `default`. Condicoes sao JSON, sem codigo:
    `{"path": "classificar.urgente", "eq": true}`, operadores `eq ne gt gte lt lte contains in exists`,
    combinaveis com `all`, `any` e `not`; tipos estritos e caminho ausente da falso.
  - *Saida estruturada*: um no `agent` com `output_schema` (subconjunto de JSON Schema: `type`, `properties`,
    `required`, `enum`, `items`) recebe o formato no prompt; a resposta e validada e, se errada, o agente roda de
    novo com os erros (`output_retries`, padrao 2).
  - *Campos*: `{{no.campo}}`, `{{no.lista[0]}}`, `{{input.campo}}`; uma string que e so uma referencia preserva o
    tipo (numero, booleano, lista). Referencia a no inexistente e erro de definicao; a campo inexistente, falha do no.
  - *Pulados e joins*: ramos nao escolhidos ficam `skipped` (e quem so depende deles tambem). `join`: `all`
    (padrao, todos os antecessores), `all_active` (os que nao foram pulados) ou `any` (o primeiro).
  - *Retomada*: o desvio escolhido fica em `workflow_node_runs.chosen_next`; o `--resume` rele a escolha e os
    pulados, sem chamar o classificador de novo.
  - *Entrada e saida*: `input_schema` valida a entrada (JSON) antes de iniciar; `output` define a saida final.
- **Suspensao duravel**: um run pode parar sem processo preso (`waiting_human`, `waiting_child`), com todo o
  historico no banco. Qualquer instancia com o despachante retoma o mesmo run quando ele fica `ready`.
  Estados: `running` -> `waiting_human` | `waiting_child` -> `ready` -> `running` -> `completed`/... ;
  suspensos podem ir direto para `cancelled`.
- **Execucao duravel** (sem Temporal):
  - *Entrega atomica*: o fim de um run, a gravacao da saida como `tool_result` do `delegate_task` no pai e a
    passagem do pai para `ready` acontecem numa unica transacao (filho `failed`/`cancelled` chega como erro da
    ferramenta). Uma queda no meio desfaz tudo; nao existe pai preso com filho concluido. Por garantia, a
    varredura do despachante ainda reconcilia qualquer pai em `waiting_child` cujo filho ja terminou.
  - *Lease por run*: ao ir para `running`, o run ganha `owner` (a instancia) e `lease_until`, renovado por
    heartbeat enquanto o modelo ou uma ferramenta executa (`RUN_LEASE_SECONDS`, padrao 60; `RUN_HEARTBEAT_SECONDS`,
    padrao 1/3 do lease). Run de conversa com lease vencido e orfao: a varredura o devolve para `ready` com o
    evento `run_recovered` (instancia anterior e nova) e ele e retomado do historico persistido. So o dono do
    lease grava resultado e mensagens; quem perde o lease para sem escrever. A conversa nunca fica "ocupada"
    para sempre: com o dono morto, o turno e recuperado e a tela segue o stream.
  - *Idempotencia de ferramenta*: antes de executar, grava `tool_call_started`. Na retomada, uma chamada que
    comecou e nao gravou resultado e repetida se a ferramenta e `side_effect = false` (leitura, busca, think,
    plano, `wait_seconds`; `delegate_task` e `ask_human` sao idempotentes pela tool call); com efeito
    (`run_shell`, `write_file`, `delete_file`, `create_task`, HTTP/MCP sem marcacao) nao repete sozinha: vira
    um pedido humano duravel "repetir, pular ou informar o resultado". `side_effect` tem padrao `true` (seguro).
  - *Delegacao idempotente*: o filho guarda `parent_tool_call_id`; retomar o pai reaproveita o filho ja criado
    (terminado: usa o resultado; andando: o pai espera por ele) em vez de criar outro.
  - Um run que caiu depois de gravar a resposta final conclui com ela, sem chamar o modelo de novo.

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

Com a execucao duravel: 12 cenarios, 76/76 checagens (inclui `durabilidade_entrega`: queda injetada dentro
da transacao de entrega e reconciliacao); `tests/run_restart.sh` com `kill -9` de verdade (chamada lenta
recuperada por outra instancia; `run_shell` interrompido nao repete e vira pergunta; dois processos
`dispatcher` disputando orfaos sem duplicar). Testes unitarios: 71.

Com aprovacao de uso unico: 12 cenarios, 77/77 checagens (com `SEARXNG_URL` apontando para o dublê; com
busca real, o tamanho dos resultados muda o ponto de compactacao do `chat` e a checagem do resumo pode
falhar); `tests/run_restart.sh` e `tests/hitl_restart.sh` OK. Testes unitarios: 74.

Com aprovacao segura em workflows (spec 001): 13 cenarios, 87/87 checagens (inclui `workflow_aprovacao`, 10/10,
com o modo global da CLI em `auto` de proposito); `tests/run_restart.sh` e `tests/hitl_restart.sh` OK;
`workflow` na linha de comando sem modo (nega), com `--approval auto` (executa) e com `APPROVAL_MODE` invalido
(erro, codigo 1). Testes unitarios: 99.

Com desvio condicional (spec 002): 14 cenarios, 99/99 checagens (inclui `workflow_desvio`, 12/12);
`tests/run_restart.sh` e `tests/hitl_restart.sh` OK; `workflow`, `retomada` e `workflow_aprovacao` iguais com o motor
refeito em volta do escalonador. Testes unitarios: 166.

Nao testado: contra o Azure Foundry real (inclusive `stream_options` e `reasoning_effort` no deployment)
e a Brave Search API. O primeiro `dotnet run -- check` com o `.env` preenchido confirma o modelo.

## Limitacoes conhecidas da POC

- `run_shell` roda no workspace do host, nao num sandbox. Em producao, execute em container efemero.
- Busca de ferramentas e da base de conhecimento e textual (tsvector). Para semantica, use pgvector.
- O budget e checado entre chamadas ao modelo, entao um run pode passar do limite em uma chamada.
- Na CLI a aprovacao e por terminal ou pelo modo global (`APPROVAL_MODE`); no worker o modo `ask` pausa tudo.
- Sem multi-tenant e sem autenticacao: o `serve` escuta so em localhost.
- Recuperacao de orfaos so para runs de conversa (`conversation_id`). Runs de `task`, `worker` e nos de
  workflow tambem tem lease, mas continuam com a recuperacao propria (retentativa da fila, `workflow --resume`).
- A recuperacao demora ate o lease vencer mais uma varredura (padrao ate ~80 s). Lease curto recupera rapido,
  mas um run que trava sem heartbeat (ex.: processo congelado) e tomado por outra instancia; a antiga para ao
  perceber que perdeu a posse, mas pode ter feito UMA chamada externa nesse intervalo.
- `side_effect` e por ferramenta, nao por argumento: `http_get` e tratado como leitura mesmo que a URL dispare
  algo no destino. Ferramenta nova sem marcacao e tratada como efeito (pergunta antes de repetir).
- O processo que cai pode deixar processos filhos rodando (ex.: o shell de um `run_shell`); a retomada nao os
  encerra, so evita repetir a chamada.
- A posse e conferida antes de cada mensagem gravada, mas nao numa mesma transacao com a gravacao: ha uma janela
  pequena em que uma instancia que acabou de perder o lease grava uma ultima mensagem.
- Texto token a token so aparece quando o run executa no mesmo processo que serve a tela; de outra instancia,
  por mensagem completa. O stream da tela le o banco a cada 250 ms.
- Tarefas da fila (`worker`) e nos de workflow ainda nao esperam pessoa. A fila usa o modo de aprovacao da CLI
  (`APPROVAL_MODE`, padrao `auto` no `serve`), exceto tarefas criadas dentro de um workflow, que usam o modo da
  execucao (padrao `deny`). Tarefas criadas pelo chat continuam no modo da CLI do worker.
- As regras `tools.allow`/`tools.block` vem da definicao atual do workflow, inclusive no `--resume` (a definicao
  nao e fixada por execucao); nem elas nem o modo sao gravados no run do agente. Quando um no puder esperar uma
  pessoa de forma duravel, o run retomado precisara recebe-los.
- Condicoes nao tem aritmetica nem funcoes (so comparacoes e combinacoes); a saida estruturada vem do prompt e da
  validacao local, sem o `response_format` do provedor; nos prontos rodam em lotes (um no `any` espera o lote
  inteiro terminar, nao so o primeiro antecessor).
- `tools.allow` nao supera `approval_when`: um `http_get` liberado para uma URL interna continua pedindo
  aprovacao (e e negado no modo padrao).
- Sem autenticacao, nao ha registro de QUEM decidiu nem aprovador por papel/notificacao (Teams, e-mail).
- DuckDuckGo Lite e raspagem de HTML e pode limitar por excesso de uso; em producao use SearXNG ou uma API.
- O MCP suporta so stdio e so tools (sem resources, prompts nem HTTP/SSE).
