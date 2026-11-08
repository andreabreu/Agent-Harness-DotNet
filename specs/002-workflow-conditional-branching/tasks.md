---

description: "Tarefas da feature 002: desvio condicional e dados estruturados em workflows"
---

# Tasks: Desvio condicional e dados estruturados em workflows

**Input**: Design documents from `/specs/002-workflow-conditional-branching/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: obrigatorios pela constituicao (Principio IV, TDD). Testes de unidade escritos e falhando antes do
codigo correspondente.

**Organization**: US1 = rotear por classificacao, US2 = retomar sem recalcular, US3 = joins com pulados,
US4 = dados estruturados (entrada tipada, `transform`, `end`, saida final).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: pode rodar em paralelo (arquivos diferentes, sem dependencia pendente)
- **[Story]**: US1–US4
- Caminhos relativos a raiz do repositorio

---

## Phase 1: Setup

- [X] T001 Rodar a linha de base e anotar em specs/002-workflow-conditional-branching/quickstart.md (secao Regressao): `dotnet test tests/AgentPoc.Tests` (esperado 99), `dotnet run -- demo all` com `SEARXNG_URL=http://localhost:8765/fake` (esperado 13 cenarios, 87/87), `tests/run_restart.sh` e `tests/hitl_restart.sh` (`RESULTADO: OK`)

---

## Phase 2: Foundational

**Purpose**: valores com caminhos, definicao validada, escalonador puro e motor refeito em volta deles, com o
comportamento atual intacto.

**⚠️ CRITICAL**: nenhuma historia comeca antes desta fase.

- [X] T002 [P] Em sql/schema.sql, de forma idempotente: `alter table workflow_node_runs drop constraint if exists workflow_node_runs_status_check` + `add constraint ... check (status in ('running', 'completed', 'failed', 'skipped'))` e `alter table workflow_node_runs add column if not exists chosen_next text[]`; aplicar com `dotnet run -- setup` sem `--reset`
- [X] T003 [P] Testes que falham em tests/AgentPoc.Tests/WorkflowValuesTests.cs para `WorkflowValues`: `{{input}}` e `{{no}}` devolvem o texto inteiro; `{{no.campo}}`, `{{no.a.b}}` e `{{no.itens[1]}}` leem JSON da saida; `{{input.campo}}` le a entrada JSON; caminho ausente em no concluido lanca erro citando o caminho; no pulado vira vazio e caminho nele conta como ausente sem erro; string que e exatamente uma referencia preserva o tipo (numero, booleano, lista, objeto) ao renderizar JSON; referencia dentro de texto vira texto; `Resolve(path)` devolve o `JsonNode` ou "ausente"
- [X] T004 Criar src/AgentPoc/Workflows/WorkflowValues.cs (entrada, saidas por no, conjunto de pulados; `Resolve`, `RenderText`, `RenderJson`), com o regex de referencia aceitando `[A-Za-z0-9_]+(\.[A-Za-z0-9_]+|\[\d+\])*`; faz T003 passar
- [X] T005 [P] Testes que falham em tests/AgentPoc.Tests/WorkflowDefinitionTests.cs para `WorkflowDefinition.Parse` com os tipos atuais: a definicao do seed `relatorio_tecnico` e valida; `type` desconhecido, `next` para destino inexistente, `agent`/`tool` sem campo obrigatorio e `{{no_inexistente}}` em `input`/`args` geram erros no formato `<no>.<campo>: <problema>`; varios erros voltam juntos; `join` ausente vira `all`
- [X] T006 Criar src/AgentPoc/Workflows/WorkflowDefinition.cs: modelo tipado (`WorkflowNode` com id, tipo, `Next`, `Join`, JSON original), lista de antecessores, `Parse(JsonObject)` que acumula erros e lanca `WorkflowDefinitionException` com todos; faz T005 passar
- [X] T007 [P] Testes que falham em tests/AgentPoc.Tests/WorkflowSchedulerTests.cs para `WorkflowScheduler` com join `all`: grafo linear libera um no por vez; ramos paralelos saem no mesmo lote; join so libera depois de todos; no com antecessor pulado e pulado; pulados se propagam ate onde nenhum antecessor executou; nenhum pronto e nada pendente = fim; nenhum pronto com pendentes = erro de ciclo
- [X] T008 Criar src/AgentPoc/Workflows/WorkflowScheduler.cs: estados (`pending`, `running`, `completed`, `failed`, `skipped`), arestas ativas/inativas conforme [data-model.md](data-model.md#estado-do-escalonador-memoria-por-execucao), `Next()` devolvendo prontos e novos pulados; faz T007 passar
- [X] T009 Refatorar src/AgentPoc/Workflows/WorkflowEngine.cs para usar `WorkflowDefinition` (validada antes de criar `workflow_runs`), `WorkflowScheduler` (no lugar de `preds`/`pending`) e `WorkflowValues` (no lugar de `Render`/`RenderNode`); gravar nos pulados com `status = 'skipped'`; saida final = terminais executados, sem pulados; manter `crashAfter`, aprovacao e regras da 001
- [X] T010 Rodar `dotnet run -- demo workflow`, `demo retomada` e `demo workflow_aprovacao` e confirmar 6/6, 6/6 e 10/10

**Checkpoint**: motor novo com comportamento atual; testes verdes.

---

## Phase 3: User Story 1 - Rotear por classificacao (Priority: P1) 🎯 MVP

**Goal**: classificador com saida estruturada validada e no `condition` que segue o primeiro caso verdadeiro ou o
`default`, pulando os outros ramos.

**Independent Test**: `dotnet run -- demo workflow_desvio` passa as checagens de US1.

### Tests for User Story 1 ⚠️

- [X] T011 [P] [US1] Testes que falham em tests/AgentPoc.Tests/ConditionEvaluatorTests.cs: cada operador `eq ne gt gte lt lte contains in exists` (verdadeiro e falso); `all`/`any`/`not` aninhados; tipos estritos (`gt` com texto da falso, `eq` entre `1` e `"1"` da falso); caminho ausente da falso e `exists` falso; `Validate(when)` aponta operador desconhecido, objeto com dois operadores e `in` sem lista
- [X] T012 [P] [US1] Testes que falham em tests/AgentPoc.Tests/JsonSchemaLiteTests.cs: `Validate(schema, value)` aceita o JSON do classificador; aponta campo obrigatorio ausente, tipo errado, valor fora de `enum` e item de lista invalido, com o caminho (`$.categoria`); `integer` rejeita 1.5; `CheckSupported(schema)` rejeita `minimum`; `ExtractJson(text)` aceita JSON puro e bloco ```json e devolve nulo para texto sem JSON
- [X] T013 [P] [US1] Testes que falham em tests/AgentPoc.Tests/WorkflowDefinitionTests.cs para `condition`: `cases[].when` validado por `ConditionEvaluator.Validate`; destinos de `cases`/`default` devem existir; `condition` com `next` e erro; `path` para no inexistente e erro; `output_schema` fora do subconjunto e erro; `output_retries` fora de 0–5 e erro
- [X] T014 [P] [US1] Testes que falham em tests/AgentPoc.Tests/WorkflowSchedulerTests.cs: desvio concluido com `chosen_next = [b]` libera `b` e pula `c` e o que so depende de `c`; desvio sem destino escolhido pula todos os seus destinos
- [X] T015 [US1] Criar o cenario `workflow_desvio` em src/AgentPoc/Demo.cs (registrado em `Scenarios`) com o workflow de triagem gravado por upsert (`demo_triagem`: `classificar` → `rotear` → `escalar` | `juridico` | `financeiro` | `suporte` (default)) e as checagens de US1, que devem falhar: (a) "revisar o contrato" segue so `juridico` e os outros 3 ramos ficam `skipped`; (b) "duvida geral" segue `suporte` (default); (c) "contrato urgente" segue `escalar` (primeiro caso vence); (d) entrada com "formato quebrado" conclui com 2 runs do classificador; (e) "sempre quebrado" deixa `classificar` `failed` com os erros do schema e o workflow `failed`

### Implementation for User Story 1

- [X] T016 [P] [US1] Em tests/mock_llm.py, ramo no `researcher` para tarefas com "classifique": devolve JSON `{"categoria": ..., "urgente": ...}` por palavras-chave ("contrato" → juridico, "fatura" → financeiro, senao suporte; "urgente" → true); com "formato quebrado" responde texto livre quando a tarefa nao traz os erros da tentativa anterior; com "sempre quebrado" sempre responde texto livre
- [X] T017 [P] [US1] Criar src/AgentPoc/Workflows/ConditionEvaluator.cs (`Evaluate(when, WorkflowValues)` e `Validate(when)`); faz T011 passar
- [X] T018 [P] [US1] Criar src/AgentPoc/Workflows/JsonSchemaLite.cs (`Validate`, `CheckSupported`, `ExtractJson`, `DescribeForPrompt`); faz T012 passar
- [X] T019 [US1] Em src/AgentPoc/Workflows/WorkflowDefinition.cs, suportar `condition` (`cases`, `default`) e `output_schema`/`output_retries` em `agent`, com as validacoes de T013; destinos do desvio entram como arestas de saida
- [X] T020 [US1] Em src/AgentPoc/Workflows/WorkflowScheduler.cs, usar `chosen_next` para ativar/inativar as arestas de saida do desvio; faz T014 passar
- [X] T021 [US1] Em src/AgentPoc/Workflows/WorkflowEngine.cs: executar `condition` (sem modelo; grava `output` `{"case": n|"default", "next": [...]}` e `chosen_next`); no `agent` com `output_schema`, acrescentar ao prompt `JsonSchemaLite.DescribeForPrompt`, extrair e validar a resposta e, se invalida, rodar o agente de novo com o prompt original e os erros, ate `output_retries` (padrao 2); gravar a saida como o JSON validado; esgotado, no `failed` com os erros
- [X] T022 [US1] Rodar `dotnet run -- demo workflow_desvio` (dublê reiniciado) e confirmar as checagens de US1

**Checkpoint**: MVP — desvio funcionando de ponta a ponta.

---

## Phase 4: User Story 2 - Retomar sem recalcular a decisao (Priority: P1)

**Goal**: queda depois do desvio; a retomada segue o mesmo ramo sem chamar o classificador de novo.

**Independent Test**: checagens de US2 em `workflow_desvio`.

### Tests for User Story 2 ⚠️

- [X] T023 [P] [US2] Teste que falha em tests/AgentPoc.Tests/WorkflowSchedulerTests.cs: escalonador criado a partir de estado restaurado (desvio `completed` com `chosen_next`, nos `skipped`) libera exatamente os mesmos nos que a execucao original liberaria
- [X] T024 [US2] Acrescentar a `workflow_desvio` em src/AgentPoc/Demo.cs, devendo falhar: `demo_triagem` com "revisar o contrato" e `crashAfter: "rotear"`; retomada com `resumeRunId`; checagens: o mesmo ramo (`juridico`) executa, `classificar` tem 1 run (nao chamou o modelo de novo), `rotear` aparece em `ReusedNodes`, e os ramos pulados nao executam

### Implementation for User Story 2

- [X] T025 [US2] Em src/AgentPoc/Workflows/WorkflowEngine.cs, na retomada carregar `completed` (com `output` e `chosen_next`) e `skipped` de `workflow_node_runs` para o estado inicial do escalonador e para `WorkflowValues`, incluindo-os em `ReusedNodes`; faz T023 e T024 passarem

**Checkpoint**: US1 e US2 juntas.

---

## Phase 5: User Story 3 - Juntar ramos com pulados (Priority: P2)

**Goal**: joins `all_active` e `any`.

**Independent Test**: checagens de US3 em `workflow_desvio`.

### Tests for User Story 3 ⚠️

- [X] T026 [P] [US3] Testes que falham em tests/AgentPoc.Tests/WorkflowSchedulerTests.cs: `all_active` com um antecessor pulado executa apos o outro concluir; `all_active` com todos pulados e pulado; `any` executa uma unica vez quando o primeiro antecessor conclui, mesmo com outro ainda em execucao; `any` com todos pulados e pulado; `all` continua pulando quando um antecessor e pulado
- [X] T027 [P] [US3] Teste que falha em tests/AgentPoc.Tests/WorkflowDefinitionTests.cs: `join` fora de `all | all_active | any` e erro
- [X] T028 [US3] Acrescentar a `workflow_desvio` em src/AgentPoc/Demo.cs, devendo falhar: `demo_triagem` ganha `consolidar` (no `tool` `kb_search` com `join: all_active`) depois dos 4 ramos — executa uma vez apos o ramo escolhido; workflow `demo_join_any` com dois nos `tool` paralelos (`kb_search`) e um no `any` — o no `any` tem uma unica execucao

### Implementation for User Story 3

- [X] T029 [US3] Em src/AgentPoc/Workflows/WorkflowScheduler.cs e WorkflowDefinition.cs, implementar e validar `all_active` e `any`; faz T026–T028 passarem

**Checkpoint**: US1–US3.

---

## Phase 6: User Story 4 - Dados estruturados entre nos (Priority: P2)

**Goal**: `input_schema`, `transform`, `end` e `output` final.

**Independent Test**: checagens de US4 em `workflow_desvio`.

### Tests for User Story 4 ⚠️

- [X] T030 [P] [US4] Testes que falham em tests/AgentPoc.Tests/WorkflowDefinitionTests.cs: `input_schema` e `output` no topo aceitos; `input_schema` fora do subconjunto e erro; `transform` sem `output` e erro; `end` com `next` ou `status` fora de `completed | failed` e erro
- [X] T031 [P] [US4] Teste que falha em tests/AgentPoc.Tests/WorkflowSchedulerTests.cs: `Finish()` (chamado quando um `end` executa) marca todos os pendentes como pulados e nao libera mais nada
- [X] T032 [US4] Acrescentar a `workflow_desvio` em src/AgentPoc/Demo.cs, devendo falhar: (a) `demo_triagem` com `input_schema` (`texto` obrigatorio) recebendo `{"assunto": "x"}` nao inicia e lista o erro, e com `{"texto": "..."}` inicia; (b) `consolidar` vira `transform` e grava JSON com `categoria` vinda de `{{classificar.categoria}}` (tipo preservado); (c) `output` do workflow = saida de `consolidar`; (d) caso `financeiro` segue para um `end` `failed` com "Pedido recusado: financeiro" e o workflow fica `failed` com essa saida; (e) definicao com `cases[0].next` para destino inexistente nao inicia e a mensagem cita `rotear.cases[0].next`

### Implementation for User Story 4

- [X] T033 [US4] Em src/AgentPoc/Workflows/WorkflowDefinition.cs, suportar `input_schema`, `output`, `transform` e `end` com as validacoes de T030
- [X] T034 [US4] Em src/AgentPoc/Workflows/WorkflowScheduler.cs, `Finish()`; faz T031 passar
- [X] T035 [US4] Em src/AgentPoc/Workflows/WorkflowEngine.cs: validar a entrada contra `input_schema` antes de criar a execucao (entrada deve ser objeto JSON; erros listados); executar `transform` (`WorkflowValues.RenderJson`, saida JSON); executar `end` (espera o lote atual, `Finish()`, grava pulados, termina com `status` e `output` renderizado; dois `end` no lote: `failed` vence, depois ordem da definicao); calcular a saida final por `output` quando definido; faz T032 passar

**Checkpoint**: todas as historias.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T036 [P] Atualizar README.md: "Como funciona" (tipos de no, condicoes, `output_schema`, joins, `skipped`, `input_schema`, `output`, exemplo curto de triagem); "Os cenarios da demo" (`workflow_desvio`); "Limitacoes" (condicoes sem expressoes aritmeticas; saida estruturada por prompt, sem `response_format` do provedor; retomada ainda usa a definicao atual); contagens de testes
- [X] T037 Regressao completa conforme [quickstart.md](quickstart.md): `dotnet test`, `demo all`, `tests/run_restart.sh`, `tests/hitl_restart.sh`; registrar a comparacao com T001 no quickstart
- [X] T038 Revisar o Constitution Check de [plan.md](plan.md) contra o implementado, registrar desvios em "Desvios na implementacao" e marcar as tarefas concluidas
- [X] T039 Commit `feat: desvio condicional e dados estruturados em workflows` com autor André Abreu <andre.abreu@live.com>, sem co-autores e sem push

---

## Dependencies & Execution Order

### Phase Dependencies

- Setup → Foundational → US1 → US2 → US3 → US4 → Polish.
- US2 e US3 dependem de US1 (precisam do desvio para gerar pulados). US4 depende da Phase 2; T032 reaproveita o
  `demo_triagem` de US1/US3.

### Within Each User Story

- Testes antes; cada teste falhando antes da implementacao correspondente.
- Arquivos compartilhados (`WorkflowDefinition.cs`, `WorkflowScheduler.cs`, `WorkflowEngine.cs`, `Demo.cs`):
  tarefas em sequencia.

### Parallel Opportunities

- Phase 2: T002, T003, T005 e T007 juntos.
- US1: T011, T012, T013, T014 e T016 juntos; T017 e T018 juntos.
- US3: T026 e T027 juntos. US4: T030 e T031 juntos.

## Parallel Example: User Story 1

```text
T011 ConditionEvaluatorTests.cs   T012 JsonSchemaLiteTests.cs   T016 tests/mock_llm.py
T017 ConditionEvaluator.cs        T018 JsonSchemaLite.cs
```

## Implementation Strategy

### MVP First

1. Phase 1 e Phase 2 (motor refeito, comportamento igual).
2. US1: desvio de ponta a ponta. Validar.
3. US2: durabilidade do desvio (exigida pelo Principio I antes de qualquer uso real).

### Incremental Delivery

US3 e US4 em seguida; commit unico da feature (T039).
