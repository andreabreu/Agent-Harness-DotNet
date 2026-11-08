# Implementation Plan: Aprovacao segura em workflows

**Branch**: `001-workflow-safe-approval` | **Date**: 2026-11-08 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-workflow-safe-approval/spec.md`

## Summary

Workflows passam a negar por padrao ferramentas que exigem aprovacao. O modo de aprovacao deixa de ser o
estatico global e vira parametro da execucao do workflow: gravado em `workflow_runs`, reaproveitado na
retomada e aplicado por um `ModeHumanGate` com modo fixo, herdado pelos subagentes. A definicao ganha um
bloco opcional `tools.allow`/`tools.block`, resolvido dentro de `ToolPolicy.Resolve` (que continua pura)
com precedencia em que bloqueios sempre vencem. Cada decisao de politica registra a origem (`source`).
Detalhes em [research.md](research.md).

## Technical Context

**Language/Version**: C# / .NET 8

**Primary Dependencies**: Npgsql (unica); nenhuma nova

**Storage**: Postgres 16 — duas colunas novas em `workflow_runs`; bloco novo dentro de `workflows.definition`

**Testing**: xUnit (`tests/AgentPoc.Tests`), cenarios `demo` com checagens, dublê `tests/mock_llm.py`

**Target Platform**: Linux/macOS, CLI e servidor web no mesmo binario

**Project Type**: CLI + web service (projeto unico)

**Performance Goals**: sem impacto mensuravel (uma checagem de glob por chamada de ferramenta)

**Constraints**: `schema.sql` idempotente; `setup` sem `--reset`; chat, fila e demo sem mudanca de
comportamento (FR-007)

**Scale/Scope**: ~6 arquivos de producao, 1 cenario de demo, 1 ramo novo no dublê, testes de unidade

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principio | Checagem | Resultado |
|---|---|---|
| I. Estado duravel | Modo gravado na execucao e relido no `--resume` | OK |
| II. Efeito no maximo uma vez | Negar antes de `tool_call_started`; nada novo com efeito | OK |
| III. Humano no controle, padrao seguro | Padrao `deny`; bloqueios vencem; origem registrada | OK (e o objetivo) |
| IV. Teste antes, evidencia depois | Testes de unidade primeiro; cenario `workflow_aprovacao`; regressao completa | OK |
| V. Simplicidade | Reusa `ModeHumanGate`, `Glob`, `RunHooks`; sem dependencia nova; sem flag | OK |

Re-check pos-design: OK. Nenhuma violacao; Complexity Tracking vazio.

## Project Structure

### Documentation (this feature)

```text
specs/001-workflow-safe-approval/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── cli-workflow.md
│   └── workflow-definition.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
sql/
└── schema.sql                     # workflow_runs.approval_mode, approval_mode_explicit
src/AgentPoc/
├── Program.cs                     # comando workflow: modo explicito ou nulo; resto inalterado
├── Demo.cs                        # cenario workflow_aprovacao
├── Harness/
│   ├── ToolPolicy.cs              # WorkflowToolRules, precedencia, Source
│   ├── ToolExecutor.cs            # passa ctx.ToolRules; grava source; evento para workflow_allow
│   ├── RunContext.cs              # RunContext.ToolRules, RunHooks.ToolRules, ApprovalDecision.Source,
│   │                              # ModeHumanGate.For(mode, explicit)
│   └── AgentRunner.cs             # heranca de ToolRules; RunToolAsync recebe gate e regras
└── Workflows/
    ├── WorkflowApprovalMode.cs    # modo + explicito a partir de --approval / APPROVAL_MODE
    └── WorkflowEngine.cs          # modo por execucao, leitura e validacao de tools.allow/block
tests/
├── mock_llm.py                    # orquestrador: "delegue ao coder" -> delegate_task(ls -la)
└── AgentPoc.Tests/
    └── WorkflowApprovalTests.cs   # precedencia, gate de modo, validacao de padroes
README.md                          # Como funciona (workflow) e Limitacoes
```

**Structure Decision**: projeto unico existente; nenhuma pasta nova alem do arquivo de teste.

## Complexity Tracking

Sem violacoes.

## Desvios na implementacao

Constitution Check revisado apos a implementacao: continua OK. Desvios do desenho:

- **Regras na retomada**: T024 previa as regras "da definicao da execucao"; o motor ainda nao fixa a definicao
  por execucao (feature 003), entao o `--resume` usa a definicao atual. Registrado em Limitacoes no README.
- **Ordem de `approval_when`**: passou a ser avaliado antes da politica base `approval`. A decisao nao muda (as
  duas pedem aprovacao), mas a origem de uma ferramenta `approval` com `approval_when` casando passa a ser
  `approval_when`.
- **TDD do cenario**: os testes de unidade foram escritos e falharam antes do codigo; as checagens do cenario
  `workflow_aprovacao` foram escritas junto com a implementacao, porque dependem das assinaturas novas para
  compilar.
