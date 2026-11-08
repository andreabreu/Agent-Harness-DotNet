# Implementation Plan: Execucao resiliente de workflows

**Branch**: `003-workflow-resilient-execution` | **Date**: 2026-11-08 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/003-workflow-resilient-execution/spec.md`

## Summary

Execucoes de workflow ganham dono e lease (o mesmo mecanismo dos runs), versao fixa (copia da definicao) e
retomada automatica pelo despachante que ja existe. Um no interrompido retoma o proprio run do agente do
historico; um no `tool` com efeito interrompido nao repete. Nos ganham `retry`, `timeout_seconds`, `optional` e
`compensate`; o workflow ganha `timeout_seconds`, `max_parallel` e cancelamento. Tudo fica em `workflow_events`.
Detalhes em [research.md](research.md).

## Technical Context

**Language/Version**: C# / .NET 8

**Primary Dependencies**: Npgsql; nenhuma nova

**Storage**: Postgres 16 — colunas novas em `workflow_runs`, `workflow_node_runs` e `runs`; tabela `workflow_events`

**Testing**: xUnit; cenario `demo workflow_resiliente`; script `tests/workflow_restart.sh` com `kill -9`; dublê

**Target Platform**: Linux/macOS, CLI e servidor web no mesmo binario

**Project Type**: CLI + web service (projeto unico)

**Performance Goals**: orfa retomada em ate 2x o lease (SC-001); heartbeat igual ao dos runs

**Constraints**: `schema.sql` idempotente; `setup` sem `--reset`; execucoes antigas (sem copia, sem lease) continuam
retomaveis por `--resume`

**Scale/Scope**: maior feature ate aqui — motor, despachante, lease, CLI, 1 cenario, 1 script de queda

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principio | Checagem | Resultado |
|---|---|---|
| I. Estado duravel | Lease, retomada automatica, versao fixa, prazo do workflow gravado, compensacao retomavel | OK (e o objetivo) |
| II. Efeito no maximo uma vez | No `tool` com efeito interrompido nao repete; nova tentativa so antes do efeito comecar | OK |
| III. Humano no controle | Gate e regras da execucao valem na retomada (inclusive pelo despachante) | OK |
| IV. Teste antes | Pecas puras testadas antes; cenario + script com `kill -9` | OK |
| V. Simplicidade | Reusa lease, despachante, `ResumeAsync`, `ExecutePendingAsync`; sem dependencia | OK |

Desvio consciente (registrado em research D4): ate a 004, a "pergunta humana" de um no com efeito interrompido em
workflow resolve como **pular**, porque o gate de workflow nao tem pessoa; e o comportamento seguro do Principio II.

Re-check pos-design: OK.

## Project Structure

### Documentation (this feature)

```text
specs/003-workflow-resilient-execution/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── cli-and-definition.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
sql/schema.sql                              # colunas, status, workflow_events, indices
src/AgentPoc/
├── Program.cs                              # workflow --cancel
├── App.cs                                  # despachante recebe o WorkflowEngine (IWorkflowRecovery)
├── Demo.cs                                 # cenario workflow_resiliente
├── Harness/
│   ├── RunLease.cs                         # RunLeaseKeeper com comando de renovacao parametrizado
│   ├── ResumeDispatcher.cs                 # varredura e laco tambem para workflows
│   └── AgentRunner.cs                      # runs.workflow_node_id; RunToolAsync com ids estaveis e retomada
└── Workflows/
    ├── WorkflowDefinition.cs               # retry, timeout_seconds, optional, compensate, max_parallel
    ├── WorkflowScheduler.cs                # Next(limit), no opcional
    ├── NodeRetryPolicy.cs                  # pura: elegibilidade e espera
    ├── NodeResumeDecision.cs               # pura: o que fazer com um no running na retomada
    ├── WorkflowEvents.cs                   # grava workflow_events
    └── WorkflowEngine.cs                   # lease, deadline, cancelamento, tentativas, compensacao, IWorkflowRecovery
tests/
├── mock_llm.py                             # "falhe ate a tentativa N", "demore N segundos"
├── workflow_restart.sh                     # kill -9 (3 partes)
└── AgentPoc.Tests/
    ├── NodeRetryPolicyTests.cs
    ├── NodeResumeDecisionTests.cs
    ├── WorkflowSchedulerTests.cs           # + limite e opcional
    └── WorkflowDefinitionTests.cs          # + campos novos
README.md
```

**Structure Decision**: projeto unico; pecas puras novas em `Workflows/`, motor so orquestra.

## Complexity Tracking

Sem violacoes.

## Desvios na implementacao

- **US1 (D3)**: `workflow_node_runs.agent_run_id` continua gravado no fim do no (a FK para `runs` impede grava-lo
  antes do run existir). O run interrompido e achado por `runs.workflow_node_id` (o mais recente desde o inicio da
  tentativa do no), que tem o mesmo efeito.
- **US1 (D2)**: `IWorkflowRecovery.ResumeNextAsync(ct)` nao recebe o dono: o motor usa a identidade da propria
  instancia, a mesma do despachante.
- **US1 (D4)**: os ids estaveis do no `tool` sao um Guid derivado de `execucao/no/tentativa` (run) e
  `wf_<no>_<tentativa>` (chamada); a chamada da retomada usa os argumentos renderizados de novo (deterministicos),
  nao os do evento `tool_call`.
- **Teste antes (Principio IV)**: os testes de unidade (T006) vieram antes do codigo; o script de queda (T007) e o
  cenario (T008, T015) foram escritos logo depois da implementacao do motor, entao nao houve a execucao "falhando"
  registrada para eles.
