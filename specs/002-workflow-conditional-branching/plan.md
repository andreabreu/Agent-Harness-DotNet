# Implementation Plan: Desvio condicional e dados estruturados em workflows

**Branch**: `002-workflow-conditional-branching` | **Date**: 2026-11-08 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-workflow-conditional-branching/spec.md`

## Summary

O motor de workflow ganha nos `condition`, `transform` e `end`, saida estruturada validada em nos `agent`
(`output_schema` com novas tentativas), entrada tipada (`input_schema`), saida final configuravel, acesso a campos
(`{{no.campo[0]}}`) e joins `all`/`all_active`/`any` com status `skipped`. A logica de agendamento sai do
`WorkflowEngine` para um `WorkflowScheduler` puro; definicao, condicoes, schemas e valores ganham classes proprias,
todas testaveis sem banco. A escolha de cada desvio e gravada (`chosen_next`) e reaproveitada na retomada.
Detalhes em [research.md](research.md).

## Technical Context

**Language/Version**: C# / .NET 8

**Primary Dependencies**: Npgsql; nenhuma nova (condicoes e schema com codigo proprio)

**Storage**: Postgres 16 — `workflow_node_runs.status` aceita `skipped`; coluna `chosen_next text[]`

**Testing**: xUnit, cenario `demo workflow_desvio`, dublê `tests/mock_llm.py`

**Target Platform**: Linux/macOS, CLI e servidor web no mesmo binario

**Project Type**: CLI + web service (projeto unico)

**Performance Goals**: agendamento O(nos + arestas) por lote; irrelevante perto das chamadas de modelo

**Constraints**: definicoes atuais continuam validas; `schema.sql` idempotente; `setup` sem `--reset`

**Scale/Scope**: 5 classes novas em `Workflows/`, motor refeito em volta do escalonador, 1 cenario, 1 ramo no dublê

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principio | Checagem | Resultado |
|---|---|---|
| I. Estado duravel | Desvio escolhido e pulados gravados; retomada rele sem reavaliar | OK |
| II. Efeito no maximo uma vez | Nos pulados nunca executam; `end` nao interrompe no em execucao (espera o lote) | OK |
| III. Humano no controle | Politicas da 001 continuam; condicoes nao executam codigo | OK |
| IV. Teste antes | Escalonador, condicoes, schema, valores e definicao puros, com testes antes; cenario com queda e retomada | OK |
| V. Simplicidade | Sem dependencia; arvore JSON em vez de parser; subconjunto de JSON Schema | OK |

Re-check pos-design: OK. Uma mudanca de comportamento deliberada (referencia a no inexistente vira erro) esta em
[contracts/workflow-definition.md](contracts/workflow-definition.md#compatibilidade); nao viola principios.

## Project Structure

### Documentation (this feature)

```text
specs/002-workflow-conditional-branching/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── workflow-definition.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
sql/
└── schema.sql                         # workflow_node_runs: status skipped, chosen_next
src/AgentPoc/
├── Demo.cs                            # cenario workflow_desvio
└── Workflows/
    ├── WorkflowDefinition.cs          # parse + validacao (todos os erros), modelo tipado dos nos
    ├── WorkflowScheduler.cs           # puro: prontos, pulados, joins, arestas ativas/inativas
    ├── ConditionEvaluator.cs          # puro: arvore when
    ├── JsonSchemaLite.cs              # puro: subconjunto de JSON Schema (validar + descrever no prompt)
    ├── WorkflowValues.cs              # puro: caminhos, templates, tipo preservado (substitui Render/RenderNode)
    └── WorkflowEngine.cs              # orquestra: executa nos por tipo, grava checkpoint, retoma, saida final
tests/
├── mock_llm.py                        # researcher: "classifique" -> JSON; modos "formato quebrado"/"sempre quebrado"
└── AgentPoc.Tests/
    ├── WorkflowSchedulerTests.cs
    ├── ConditionEvaluatorTests.cs
    ├── JsonSchemaLiteTests.cs
    ├── WorkflowValuesTests.cs
    └── WorkflowDefinitionTests.cs
README.md
```

**Structure Decision**: projeto unico; a pasta `Workflows/` ganha as pecas puras para o motor ficar so com
orquestracao e persistencia.

## Complexity Tracking

Sem violacoes.

## Desvios na implementacao

Constitution Check revisado apos a implementacao: continua OK. Desvios do desenho e das tarefas:

- **Ordem das fases**: os testes de unidade de todas as historias (escalonador com desvio e joins, condicoes, schema,
  validacoes de `condition`/`transform`/`end`) foram escritos antes do codigo, mas juntos, e a definicao e o
  escalonador foram implementados de uma vez em vez de crescer historia a historia. Todos falharam antes (nao
  compilavam) e passaram depois.
- **Checagens do cenario**: `workflow_desvio` foi escrito com o motor ja pronto, entao as checagens nao foram vistas
  falhando antes; a prova da falha ficou nos testes de unidade.
- **`consolidar`**: T028 previa um no `tool` trocado por `transform` em T032; o cenario ja nasceu com `transform`.
- **Join `any`**: o motor executa em lotes; o no `any` roda uma unica vez, mas so depois que o lote dos antecessores
  termina, nao no instante em que o primeiro conclui. Registrado em Limitacoes no README.
- **Cache de JSON em `WorkflowValues`**: removido; nos paralelos leem os valores ao mesmo tempo e um `JsonNode`
  interpretado nao e seguro para leitura concorrente na primeira materializacao. Cada leitura interpreta de novo.
