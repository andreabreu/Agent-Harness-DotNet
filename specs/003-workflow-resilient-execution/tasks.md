---

description: "Tarefas da feature 003: execucao resiliente de workflows"
---

# Tasks: Execucao resiliente de workflows

**Input**: Design documents from `/specs/003-workflow-resilient-execution/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: obrigatorios (Principio IV). Testes de unidade antes do codigo; garantias de queda com `kill -9`.

**Organization**: US1 = retomada automatica apos queda, US2 = versao fixa, US3 = tentativas, prazos, opcional e
paralelismo, US4 = cancelar e compensar.

## Format: `[ID] [P?] [Story] Description`

---

## Phase 1: Setup

- [X] T001 Linha de base em specs/003-workflow-resilient-execution/quickstart.md (secao Regressao): `dotnet test` (166), `demo all` com `SEARXNG_URL` no dublê (14 cenarios, 99/99), `tests/run_restart.sh` e `tests/hitl_restart.sh` OK

---

## Phase 2: Foundational

**⚠️ CRITICAL**: nenhuma historia comeca antes desta fase.

- [X] T002 [P] Em sql/schema.sql (idempotente): `workflow_runs` — check de status com `running ready completed failed crashed cancelled expired compensation_failed`; colunas `owner text`, `lease_until timestamptz`, `definition jsonb`, `deadline_at timestamptz`, `cancel_requested_at timestamptz`; indices `ix_workflow_runs_lease (lease_until) where status = 'running'` e `ix_workflow_runs_ready (started_at) where status = 'ready'`. `workflow_node_runs.compensated_at timestamptz`. `runs.workflow_node_id text`. Tabela `workflow_events (id bigserial primary key, workflow_run_id uuid not null references workflow_runs(id), type text not null, node_id text, data jsonb not null default '{}', created_at timestamptz not null default now())` com indice `(workflow_run_id, id)`. Aplicar com `setup` sem `--reset`
- [X] T003 [P] Criar src/AgentPoc/Workflows/WorkflowEvents.cs: `AddAsync(IDbExecutor, workflowRunId, type, nodeId?, JsonObject data)` e constantes dos tipos de [contracts/cli-and-definition.md](contracts/cli-and-definition.md#eventos-workflow_eventstype)
- [X] T004 Em src/AgentPoc/Harness/RunLease.cs, `RunLeaseKeeper.Start` passa a receber o comando de renovacao (SQL + parametros) e ganha as fabricas `ForRun(db, runId, owner, options, ct)` (comportamento atual) e `ForWorkflow(db, workflowRunId, owner, options, ct)` (`update workflow_runs set lease_until = ... where id and owner and status = 'running'`); o keeper de workflow tambem expoe `CancelRequested` lido de `cancel_requested_at` a cada renovacao. Atualizar o uso em src/AgentPoc/Harness/AgentRunner.cs
- [X] T005 Rodar `dotnet test` e `demo workflow`, `retomada`, `workflow_aprovacao`, `workflow_desvio` (iguais a linha de base) e `tests/run_restart.sh` (lease dos runs intacto)

---

## Phase 3: User Story 1 - Retomada automatica apos queda (Priority: P1) 🎯 MVP

**Goal**: execucao orfa (lease vencido) e retomada por outra instancia, uma unica vez; no de agente continua o
proprio run; no `tool` com efeito interrompido nao repete.

**Independent Test**: `tests/workflow_restart.sh` partes 1 a 3 e as checagens de US1 em `workflow_resiliente`.

### Tests for User Story 1 ⚠️

- [X] T006 [P] [US1] Testes que falham em tests/AgentPoc.Tests/NodeResumeDecisionTests.cs para `NodeResumeDecision.For(runStatus?, leaseUntil?, now)` conforme a tabela de [data-model.md](data-model.md#decisao-de-retomada-de-um-no-running-resumedecision-pura): sem run → `RunFromScratch`; `completed`/`failed`/`cancelled`/`budget_exceeded` → `UseResult`; `running` com lease vencido ou `ready` → `TakeOverAndResume`; `running` com lease valido → `Wait`
- [X] T007 [US1] Criar `tests/workflow_restart.sh` (no estilo de tests/run_restart.sh, `RUN_LEASE_SECONDS=4`): (1) `workflow` de um no `agent` com o dublê "espere 8 segundos" iniciado em processo proprio; `kill -9` depois de `tool_call_started` de `wait_seconds`; um `dispatcher` em outro processo; checa `workflow_recovered` com pids diferentes, o MESMO `agent_run_id` concluido, workflow `completed`; (2) no `tool` `run_shell` "comando lento" com `--approval auto`; `kill -9` depois de `tool_call_started`; checa que `run_shell` comecou uma vez, `tool_call_recovered` com decisao `Skip`, e o arquivo de efeito com no maximo uma linha; (3) tres execucoes com no "espere 10 segundos", `kill -9`, dois `dispatcher`: cada uma com um `workflow_recovered` e `completed`. O script deve falhar antes da implementacao
- [X] T008 [US1] Acrescentar o cenario `workflow_resiliente` em src/AgentPoc/Demo.cs (registrado em `Scenarios`) com a checagem de US1, devendo falhar: execucao de `demo_resiliente_orfa` (dois nos `tool` `kb_search` em sequencia) com o primeiro no `completed` e a execucao forjada como `running`, `owner = 'instancia-morta'`, `lease_until` no passado; com o despachante desta instancia ativo, ela termina `completed`, tem `workflow_recovered` com `previous_owner = 'instancia-morta'`, e o primeiro no tem `attempt = 1`

### Implementation for User Story 1

- [X] T009 [P] [US1] Criar src/AgentPoc/Workflows/NodeResumeDecision.cs; faz T006 passar
- [X] T010 [US1] Em src/AgentPoc/Harness/AgentRunner.cs: `RunAsync` aceita `workflowNodeId` e grava `runs.workflow_node_id`; `RunToolAsync` recebe `runId`, `callId` e `workflowNodeId` estaveis e, se o pseudo-run ja existe com `tool_call_started` sem `tool_result`, assume o run e usa `ToolExecutor.ExecutePendingAsync` com a chamada reconstruida do evento `tool_call`; se ja tem `tool_result`, devolve o resultado gravado
- [X] T011 [US1] Em src/AgentPoc/Workflows/WorkflowEngine.cs: lease da execucao com `RunLeaseKeeper.ForWorkflow` (dono = `AgentRunner.InstanceId`) durante o laco, parando sem escrever ao perder a posse; gravar `agent_run_id` (id gerado antes) no inicio de cada no; na retomada, aplicar `NodeResumeDecision` aos nos `running` (assumir o run com `owner`/`lease_until` e chamar `AgentRunner.ResumeAsync` com o gate e as regras da execucao; usar o resultado; ou executar do zero); `--resume` manual recusado se o lease ainda e valido
- [X] T012 [US1] Em src/AgentPoc/Harness/ResumeDispatcher.cs, criar e usar a interface `IWorkflowRecovery` (`RecoverOrphansAsync(by)`, `ResumeNextAsync(owner, ct)`), implementada no `WorkflowEngine`: orfas `running` com lease vencido viram `ready` com `workflow_recovered` (`previous_owner`, `lease_expired_at`, `recovered_by`) e `NOTIFY`; o laco pega `ready` com `FOR UPDATE SKIP LOCKED`, assume o lease e chama `RunAsync(resumeRunId)`; limite de 2 execucoes de workflow simultaneas por instancia. Ligar em src/AgentPoc/App.cs
- [X] T013 [P] [US1] Em tests/mock_llm.py, garantir que "espere N segundos" funcione para agente de no de workflow (o `assistant` ja tem o ramo; usar o mesmo agente no no)
- [X] T014 [US1] Rodar `tests/workflow_restart.sh` e `demo workflow_resiliente` e confirmar US1

**Checkpoint**: MVP — workflows sobrevivem a `kill -9` sem intervencao.

---

## Phase 4: User Story 2 - Versao fixa (Priority: P1)

**Goal**: cada execucao termina com a definicao com que comecou.

**Independent Test**: checagem de US2 em `workflow_resiliente`.

### Tests for User Story 2 ⚠️

- [X] T015 [US2] Acrescentar a `workflow_resiliente` em src/AgentPoc/Demo.cs, devendo falhar: `demo_resiliente_versao` v1 (nos `a` → `b`) roda com `crashAfter: "a"`; a definicao e trocada para v2 (`b` vira `b2`); a retomada termina executando `b` (v1); uma execucao nova executa `b2`; uma execucao forjada sem `definition` retoma com a definicao atual e registra `definition_not_pinned`

### Implementation for User Story 2

- [X] T016 [US2] Em src/AgentPoc/Workflows/WorkflowEngine.cs: gravar `workflow_runs.definition` na criacao; na retomada e em `HooksForRunAsync` usar a copia (sem copia: definicao atual + `definition_not_pinned`); faz T015 passar
- [X] T017 [US2] Atualizar o README: remover a limitacao "regras vem da definicao atual no `--resume`" registrada na 001

**Checkpoint**: US1 e US2.

---

## Phase 5: User Story 3 - Falhas transitorias, prazos, opcional e paralelismo (Priority: P2)

**Goal**: `retry`, `timeout_seconds` (no e workflow), `optional`, `max_parallel`.

**Independent Test**: checagens de US3 em `workflow_resiliente`.

### Tests for User Story 3 ⚠️

- [X] T018 [P] [US3] Testes que falham em tests/AgentPoc.Tests/NodeRetryPolicyTests.cs para `NodeRetryPolicy`: padrao = 1 tentativa; `ShouldRetry(attempt, reason, effectStarted, sideEffect)` respeita `max_attempts` e `on`; ferramenta com efeito so repete se o efeito nao comecou; `Wait(attempt)` = backoff * factor^(attempt-1) (1 s, 2 s, 4 s com o padrao)
- [X] T019 [P] [US3] Testes que falham em tests/AgentPoc.Tests/WorkflowSchedulerTests.cs: `Next(limit: 2)` com 4 prontos libera 2 e depois mais 2 conforme concluem; no opcional que falhou (`CompleteOptionalFailure`) mantem as arestas ativas
- [X] T020 [P] [US3] Testes que falham em tests/AgentPoc.Tests/WorkflowDefinitionTests.cs: `retry` (`max_attempts` 1–10, `backoff_seconds` >= 0, `factor` >= 1, `on` so `error`/`timeout`), `timeout_seconds` > 0 no no e no topo, `optional` booleano, `max_parallel` 1–50, `compensate` com `type` `tool`/`agent` e sem `next`; cada um com erro no formato `<no>.<campo>: <problema>`
- [X] T021 [P] [US3] Em tests/mock_llm.py, endpoint `GET /fake/flaky?key=K&until=N` que responde HTTP 503 nas primeiras N-1 chamadas de cada `key` e 200 depois (o `http_get` transforma 503 em `ERRO: HTTP 503`, falha do no `tool`); o cenario usa `--approval auto` explicito, porque URL interna cai em `approval_when`
- [X] T022 [US3] Acrescentar a `workflow_resiliente` em src/AgentPoc/Demo.cs, devendo falhar: (a) no `tool` `http_get` para `/fake/flaky?until=3` com `retry.max_attempts = 3`, `backoff_seconds = 0.2`, que falha nas 2 primeiras: `completed`, `attempt = 3`, 2 `node_retry` com esperas 0.2 e 0.4; (b) no `agent` com `timeout_seconds = 2` e o dublê "espere 5 segundos": `node_timeout`, no `failed`, run `cancelled`; (c) no `optional` que falha: `node_failed_optional`, workflow `completed`, no seguinte executou; (d) workflow com `timeout_seconds = 2` e no lento: `expired`, `workflow_expired`; (e) `max_parallel = 2` com 4 nos `tool` paralelos: nunca mais de 2 com intervalos de execucao sobrepostos
- [X] T023 [P] [US3] Criar src/AgentPoc/Workflows/NodeRetryPolicy.cs; faz T018 passar
- [X] T024 [US3] Em src/AgentPoc/Workflows/WorkflowScheduler.cs, `Next(int? limit)` e falha de no opcional; faz T019 passar
- [X] T025 [US3] Em src/AgentPoc/Workflows/WorkflowDefinition.cs, validar e expor os campos novos; faz T020 passar
- [X] T026 [US3] Em src/AgentPoc/Workflows/WorkflowEngine.cs: tentativas com `NodeRetryPolicy` (evento `node_retry`, `Task.Delay` da espera, `attempt` incrementado); prazo do no com `CancellationTokenSource` ligado (evento `node_timeout`); `deadline_at` gravado na criacao e verificado a cada lote e no token (status `expired`, evento `workflow_expired`, nos em execucao cancelados); no opcional; `max_parallel`; faz T022 passar

**Checkpoint**: US1–US3.

---

## Phase 6: User Story 4 - Cancelar e compensar (Priority: P3)

**Goal**: `workflow --cancel` e `compensate` em ordem inversa.

**Independent Test**: checagens de US4 em `workflow_resiliente`.

### Tests for User Story 4 ⚠️

- [X] T027 [P] [US4] Testes que falham em tests/AgentPoc.Tests/WorkflowSchedulerTests.cs (ou em um `CompensationPlan` puro): ordem de compensacao = nos concluidos com `compensate`, ordem inversa de conclusao, sem os ja compensados
- [X] T028 [US4] Acrescentar a `workflow_resiliente` em src/AgentPoc/Demo.cs, devendo falhar: (a) cancelamento pedido durante um no lento (agente "espere 5 segundos"): `cancelled`, `workflow_cancelled`, run do agente `cancelled`, no seguinte nunca iniciou; (b) cancelamento de execucao `ready`/orfa: marcada `cancelled` pela varredura sem ser retomada; (c) A e B (tool `kb_search`) com `compensate` e C falhando: `compensation_started`, `node_compensated` de B e depois de A, workflow `failed`; (d) compensacao que falha: `compensation_failed`

### Implementation for User Story 4

- [X] T029 [US4] Em src/AgentPoc/Workflows/WorkflowEngine.cs: `CancelAsync(workflowRunId)` grava `cancel_requested_at` e `NOTIFY`; o keeper sinaliza `CancelRequested` e o motor cancela o token, marca `cancelled` e registra `workflow_cancelled`; a varredura de `IWorkflowRecovery` cancela `ready`/orfas com pedido; compensacao com `compensated_at` (retomavel), eventos e status `compensation_failed`; faz T027 e T028 passarem
- [X] T030 [US4] Em src/AgentPoc/Program.cs, `workflow --cancel <id>` (codigo 1 para execucao terminada) e ajuda atualizada

**Checkpoint**: todas as historias.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T031 [P] Atualizar README.md: "Como funciona" (lease e retomada automatica de workflows, versao fixa, `retry`, prazos, `optional`, `max_parallel`, cancelamento, compensacao, `workflow_events`); comandos (`--cancel`); cenarios (`workflow_resiliente`) e scripts (`tests/workflow_restart.sh`); "Limitacoes" (espera entre tentativas em memoria; pergunta de no com efeito resolve como pular ate a 004; compensacao nao desfaz efeitos de agentes, so roda o no declarado)
- [X] T032 Regressao completa: `dotnet test`, `demo all`, `tests/run_restart.sh`, `tests/hitl_restart.sh`, `tests/workflow_restart.sh`; registrar a comparacao com T001 no quickstart
- [X] T033 Revisar o Constitution Check do plan.md, registrar desvios em "Desvios na implementacao" e marcar as tarefas concluidas
- [X] T034 Commit `feat: execucao resiliente de workflows` com autor André Abreu <andre.abreu@live.com>, sem co-autores e sem push

---

## Dependencies & Execution Order

- Setup → Foundational → US1 → US2 → US3 → US4 → Polish.
- US2 depende de US1 (retomada pelo despachante usa a copia). US3 e US4 dependem da Phase 2; US4 usa o keeper de T004.
- Arquivos compartilhados (`WorkflowEngine.cs`, `Demo.cs`, `WorkflowDefinition.cs`, `WorkflowScheduler.cs`):
  tarefas em sequencia.

### Parallel Opportunities

- Phase 2: T002 e T003. US1: T006, T009 e T013. US3: T018, T019, T020, T021 e T023. US4: T027.

## Implementation Strategy

### MVP First

Phase 1–2, US1 e US2 (as duas P1): workflows sobrevivem a queda e terminam na versao com que comecaram. Validar com
`tests/workflow_restart.sh`. Pode virar um commit proprio antes de US3/US4.

### Incremental Delivery

US3 (tentativas, prazos, opcional, paralelismo) e US4 (cancelar e compensar) em seguida; commit unico da feature
ou um por fase, conforme a constituicao (commits pequenos).
