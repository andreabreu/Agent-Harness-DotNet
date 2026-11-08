---

description: "Tarefas da feature 001: aprovacao segura em workflows"
---

# Tasks: Aprovacao segura em workflows

**Input**: Design documents from `/specs/001-workflow-safe-approval/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: obrigatorios pela constituicao (Principio IV, TDD). Em cada historia, os testes vem antes e MUST
falhar antes da implementacao.

**Organization**: tarefas agrupadas por historia do spec (US1 = padrao seguro, US2 = politica por workflow).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: pode rodar em paralelo (arquivos diferentes, sem dependencia pendente)
- **[Story]**: historia a que a tarefa pertence (US1, US2)
- Caminhos relativos a raiz do repositorio

---

## Phase 1: Setup

**Purpose**: registrar a linha de base antes de mexer.

- [X] T001 Rodar a linha de base e anotar os numeros em specs/001-workflow-safe-approval/quickstart.md (secao Regressao): `dotnet test tests/AgentPoc.Tests` (esperado 74), `dotnet run -- demo all` com `SEARXNG_URL=http://localhost:8765/fake` e dublê ativo (esperado 12 cenarios, 77/77), `tests/run_restart.sh` e `tests/hitl_restart.sh` (esperado `RESULTADO: OK`)

---

## Phase 2: Foundational

**Purpose**: origem estruturada das decisoes e colunas novas; usadas pelas duas historias.

**⚠️ CRITICAL**: nenhuma historia comeca antes desta fase.

- [X] T002 [P] Escrever testes que falham em tests/AgentPoc.Tests/WorkflowApprovalTests.cs para a origem de `ToolPolicy.Resolve` sem regras de workflow: politica `deny` → `Source = "tool_policy"`; `deny_when` → `"deny_when"`; `approval` → `"tool_policy"`; `approval_when` → `"approval_when"`; `auto` → `"tool_policy"`
- [X] T003 Adicionar `string Source` a `PolicyResult` em src/AgentPoc/Harness/ToolPolicy.cs e preencher em cada retorno de `Resolve` conforme T002 (os testes existentes em tests/AgentPoc.Tests continuam passando)
- [X] T004 [P] Adicionar `string Source = "human"` como ultimo parametro de `ApprovalDecision` em src/AgentPoc/Harness/RunContext.cs (valores possiveis: `human`, `mode_default`, `mode_explicit`)
- [X] T005 Gravar no evento `policy` de src/AgentPoc/Harness/ToolExecutor.cs o campo `source` (para `blocked`: `policy.Source`; para `approved`/`denied`: `decision.Source`) e, quando houve aprovacao, `policy_source = policy.Source`
- [X] T006 [P] Adicionar em sql/schema.sql, de forma idempotente, `alter table workflow_runs add column if not exists approval_mode text` com check `approval_mode in ('deny','auto','ask')` (via `drop constraint if exists` + `add constraint`) e `alter table workflow_runs add column if not exists approval_mode_explicit boolean not null default false`; validar com `dotnet run -- setup` (sem `--reset`) sobre o banco existente

**Checkpoint**: `dotnet test` verde; `demo all` igual a linha de base.

---

## Phase 3: User Story 1 - Workflow nao aprova sozinho (Priority: P1) 🎯 MVP

**Goal**: sem modo informado, ferramentas de politica `approval` sao negadas em workflows (nos `agent`, seus subagentes e nos `tool`); modo explicito continua possivel; o modo fica gravado e e reaproveitado na retomada.

**Independent Test**: `dotnet run -- demo workflow_aprovacao` passa as checagens de US1 sem nenhuma configuracao de aprovacao.

### Tests for User Story 1 ⚠️

- [X] T007 [P] [US1] Testes que falham em tests/AgentPoc.Tests/WorkflowApprovalTests.cs para `ModeHumanGate.For(mode, explicit)`: `For("deny", false).ApproveAsync` nega com motivo "negado por padrao em workflow (sem modo de aprovacao informado)" e `Source = "mode_default"`; `For("auto", true)` aprova com `Source = "mode_explicit"`; `For("deny", true)` nega com `Source = "mode_explicit"`; `AskAsync` fora do modo `ask` devolve `ModeHumanGate.NoHumanAnswer`
- [X] T008 [P] [US1] Testes que falham em tests/AgentPoc.Tests/WorkflowApprovalTests.cs para `WorkflowApprovalMode.From(flag, env)`: flag `auto` → (`auto`, explicito); so env `ask` → (`ask`, explicito); flag vence env; nenhum → (`deny`, nao explicito); valor fora de `deny|auto|ask` → erro listando os valores aceitos
- [X] T009 [US1] Criar o cenario `workflow_aprovacao` em src/AgentPoc/Demo.cs (registrado na lista de cenarios e no `all`) com as checagens de US1, que devem falhar antes da implementacao: (a) workflow `demo_aprovacao_tool` (no `tool` com `run_shell` `echo aprovado`) sem modo → no falha, evento `policy` com `decision = denied` e `source = mode_default`, nenhum `tool_call_started` de `run_shell`; (b) o mesmo com `WorkflowApprovalMode` explicito `auto` → executa, `source = mode_explicit`; (c) workflow `demo_aprovacao_agente` (no `agent` `orchestrator` com entrada "delegue ao coder: rode ls -la no shell") sem modo → o `run_shell` do subagente `coder` e negado com `source = mode_default`; (d) `workflow_runs.approval_mode = 'deny'` e `approval_mode_explicit = false` na execucao (a). Os workflows sao gravados por upsert em `workflows` no inicio do cenario

### Implementation for User Story 1

- [X] T010 [P] [US1] Em tests/mock_llm.py, ramo novo no `orchestrator`: se a tarefa contem "delegue ao coder", chamar `delegate_task` para `coder` com a tarefa "rode ls -la no shell" e, apos o resultado, responder com o texto do resultado (reaproveita o ramo `ls -la` do `coder`)
- [X] T011 [US1] Em src/AgentPoc/Harness/RunContext.cs, dar ao `ModeHumanGate` um construtor privado (fonte do modo, explicito) e a fabrica `ModeHumanGate.For(string mode, bool explicitMode)`; `ModeHumanGate.Instance` continua lendo `Approval.Mode` com `Source = "mode_explicit"` (chat, fila e demo inalterados); padrao nao explicito `deny` usa o motivo de T007; faz T007 passar
- [X] T012 [P] [US1] Criar src/AgentPoc/Workflows/WorkflowApprovalMode.cs: `sealed record WorkflowApprovalMode(string Mode, bool Explicit)` com `From(string? flag, string? env)` (regras de [contracts/cli-workflow.md](contracts/cli-workflow.md)), constante `Default = deny` e `Gate()` retornando `ModeHumanGate.For(Mode, Explicit)`; faz T008 passar
- [X] T013 [US1] Em src/AgentPoc/Harness/AgentRunner.cs, `RunToolAsync` recebe `IHumanGate human` e o atribui a `RunContext.Human`
- [X] T014 [US1] Em src/AgentPoc/Workflows/WorkflowEngine.cs, `RunAsync` recebe `WorkflowApprovalMode? approval`: na criacao grava `approval_mode`/`approval_mode_explicit` (nulo → `WorkflowApprovalMode.Default`); no `--resume` le os valores gravados (nulos → padrao `deny`) e, se `approval` explicito foi passado, atualiza as colunas e registra `Log.Warn` da troca; loga `aprovacao: <modo> (padrao de workflow|explicito)`; passa `new RunHooks(null, gate)` ao `RunAsync` dos nos `agent` e `gate` ao `RunToolAsync` dos nos `tool`
- [X] T015 [US1] Em src/AgentPoc/Program.cs, no comando `workflow`, montar `WorkflowApprovalMode.From(Opt("approval"), Env.Get("APPROVAL_MODE"))` (sem o fallback `auto` da linha global), tratar erro de valor invalido com mensagem e codigo 1, e repassar a `app.Workflows.RunAsync`; os outros comandos ficam como estao
- [X] T016 [US1] Rodar `dotnet run -- demo workflow_aprovacao` e confirmar as checagens de US1; rodar `dotnet run -- demo workflow` e `dotnet run -- demo retomada` (nao usam ferramentas de aprovacao) para confirmar que continuam passando com o novo padrao

**Checkpoint**: US1 entregue e testavel sozinha (MVP).

---

## Phase 4: User Story 2 - Politica por workflow (Priority: P2)

**Goal**: a definicao aceita `tools.allow`/`tools.block` com curinga; bloqueios sempre vencem; vale para nos e subagentes; padroes invalidos impedem o inicio.

**Independent Test**: `dotnet run -- demo workflow_aprovacao` passa as checagens de US2.

### Tests for User Story 2 ⚠️

- [X] T017 [P] [US2] Testes que falham em tests/AgentPoc.Tests/WorkflowApprovalTests.cs para `ToolPolicy.Resolve(..., WorkflowToolRules rules)` cobrindo a tabela de precedencia de [data-model.md](data-model.md): (1) global `deny` com `allow` → Deny `tool_policy`; (2) `deny_when` com `allow` → Deny `deny_when`; (3) `block` sobre ferramenta `auto` → Deny `workflow_block`; (4) `approval_when` casando com `allow` → Approval `approval_when`; (5) `approval` com `allow` → Auto `workflow_allow`; (6) mesma ferramenta em `allow` e `block` → Deny `workflow_block`; (7) curinga `mcp_fs_*` casa `mcp_fs_read`; (8) sem regras → resultado igual ao atual
- [X] T018 [P] [US2] Testes que falham em tests/AgentPoc.Tests/WorkflowApprovalTests.cs para `WorkflowToolRules.Parse(definition)` (sem bloco → `null`; listas ausentes → vazias) e `WorkflowToolRules.UnmatchedPatterns(registeredToolNames)` (devolve os padroes sem nenhuma ferramenta correspondente; padrao repetido nas duas listas nao e erro)
- [X] T019 [US2] Acrescentar ao cenario `workflow_aprovacao` em src/AgentPoc/Demo.cs as checagens de US2, que devem falhar antes da implementacao: (a) `demo_aprovacao_allow` (`allow: ["run_shell"]`, no `tool` `run_shell` `echo liberado`) sem modo → executa, evento `policy` com `decision = approved` e `source = workflow_allow`; (b) `demo_aprovacao_block` (`block: ["kb_search"]`, no `tool` `kb_search`) → bloqueado com `source = workflow_block`; (c) `demo_aprovacao_deny_when` (`allow: ["run_shell"]`, `run_shell` `sudo ls`) → bloqueado com `source = deny_when`; (d) `demo_aprovacao_heranca` (`allow: ["run_shell"]`, no `agent` `orchestrator` "delegue ao coder: rode ls -la no shell") → o `run_shell` do `coder` executa com `source = workflow_allow` e o `delete_file` continua bloqueado; (e) `demo_aprovacao_invalido` (`block: ["ferramenta_inexistente"]`) → `RunAsync` falha antes de criar `workflow_runs` e a mensagem cita `ferramenta_inexistente`

### Implementation for User Story 2

- [X] T020 [US2] Em src/AgentPoc/Harness/ToolPolicy.cs, criar `sealed record WorkflowToolRules(string[] Allow, string[] Block)` com `Parse(JsonObject definition)` e `UnmatchedPatterns(IEnumerable<string>)` (usando `Glob.Match`), e adicionar o parametro opcional `WorkflowToolRules? rules` a `Resolve` na ordem de precedencia do [data-model.md](data-model.md); faz T017 e T018 passarem
- [X] T021 [US2] Em src/AgentPoc/Harness/RunContext.cs, adicionar `WorkflowToolRules? ToolRules { get; init; }` ao `RunContext` e `WorkflowToolRules? ToolRules = null` ao final de `RunHooks`
- [X] T022 [US2] Em src/AgentPoc/Harness/AgentRunner.cs, preencher `ToolRules = hooks?.ToolRules ?? parent?.ToolRules` em `RunAsync` (heranca pelos subagentes) e fazer `RunToolAsync` receber `WorkflowToolRules? rules` e atribuir ao contexto
- [X] T023 [US2] Em src/AgentPoc/Harness/ToolExecutor.cs, passar `ctx.ToolRules` a `ToolPolicy.Resolve` (nas duas chamadas, incluindo a reavaliacao de argumentos editados) e, quando `policy.Source == "workflow_allow"`, gravar evento `policy` com `decision = "approved"`, `reason = "liberado pelo workflow"` e `source = "workflow_allow"` antes de executar
- [X] T024 [US2] Em src/AgentPoc/Workflows/WorkflowEngine.cs, ler a definicao antes de criar `workflow_runs`, fazer `WorkflowToolRules.Parse`, validar com `UnmatchedPatterns` contra `select name from tools` (erro `InvalidOperationException` citando os padroes, sem criar execucao) e passar as regras em `new RunHooks(null, gate, rules)` e a `RunToolAsync`; na retomada, usar as regras da definicao da execucao
- [X] T025 [US2] Rodar `dotnet run -- demo workflow_aprovacao` e confirmar todas as checagens de US1 e US2

**Checkpoint**: US1 e US2 funcionando juntas.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [X] T026 [P] Atualizar README.md: em "Como funciona" (workflow), o modo de aprovacao por execucao, padrao `deny`, bloco `tools.allow`/`tools.block` e precedencia; em "Limitacoes", que regras de workflow nao sao persistidas no run (necessario quando a 004 trouxer espera duravel em nos) e que `allow` nao supera `approval_when`; atualizar a contagem de testes e cenarios
- [X] T027 [P] Atualizar o comentario de `APPROVAL_MODE` em .env.example dizendo que, no comando `workflow`, defini-lo conta como modo explicito
- [X] T028 Regressao completa conforme [quickstart.md](quickstart.md): `dotnet test tests/AgentPoc.Tests`, `dotnet run -- demo all` (com `SEARXNG_URL` no dublê), `tests/run_restart.sh` e `tests/hitl_restart.sh`; comparar com a linha de base de T001 e registrar a saida
- [X] T029 Revisar o Constitution Check de [plan.md](plan.md) contra o que foi implementado e marcar as tarefas concluidas neste arquivo
- [X] T030 Commit unico `feat: aprovacao segura em workflows (padrao deny e politica por workflow)` com autor André Abreu <andre.abreu@live.com>, sem co-autores e sem push

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: sem dependencias.
- **Foundational (Phase 2)**: depende de T001; bloqueia US1 e US2.
- **US1 (Phase 3)**: depende da Phase 2.
- **US2 (Phase 4)**: depende da Phase 2; na pratica reaproveita T013/T014 (assinaturas de `RunToolAsync` e `RunAsync` do motor), entao fazer depois de US1.
- **Polish (Phase 5)**: depende de US1 e US2.

### Within Each User Story

- Testes (T007–T009, T017–T019) escritos e falhando antes da implementacao.
- US1: T011 → T012 → T013 → T014 → T015 → T016 (T010 em paralelo a qualquer momento).
- US2: T020 → T021 → T022 → T023 → T024 → T025.

### Parallel Opportunities

- Phase 2: T002, T004 e T006 juntos (arquivos diferentes).
- US1: T007, T008 e T010 juntos; T012 em paralelo a T011.
- US2: T017 e T018 juntos.
- Polish: T026 e T027 juntos.

## Parallel Example: User Story 1

```text
T007 testes de ModeHumanGate.For          -> tests/AgentPoc.Tests/WorkflowApprovalTests.cs
T008 testes de WorkflowApprovalMode.From  -> tests/AgentPoc.Tests/WorkflowApprovalTests.cs (mesmo arquivo: escrever em sequencia)
T010 ramo "delegue ao coder" no dublê     -> tests/mock_llm.py
```

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1 e Phase 2.
2. Phase 3 (US1): fecha a brecha de aprovacao automatica.
3. Validar com `demo workflow_aprovacao` e a regressao; ja e entregavel.

### Incremental Delivery

1. US1 → valida → (pode ser commitado sozinho, se desejado).
2. US2 → valida → commit unico da feature (T030) conforme a constituicao.
