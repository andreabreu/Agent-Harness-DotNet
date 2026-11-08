# Quickstart: validar desvio condicional

## Pre-requisitos

Os mesmos da 001 ([quickstart](../001-workflow-safe-approval/quickstart.md#pre-requisitos)), com o dublê
reiniciado depois de atualizar `tests/mock_llm.py`.

## 1. Testes de unidade

```bash
dotnet test tests/AgentPoc.Tests
```

Esperado: os 99 atuais e os novos de `ConditionEvaluator`, `JsonSchemaLite`, `WorkflowValues`,
`WorkflowScheduler` e `WorkflowDefinition`.

## 2. Cenario de ponta a ponta

```bash
cd src/AgentPoc && dotnet run -- demo workflow_desvio
```

| Situacao | Resultado esperado |
|---|---|
| pedido sobre contrato | so o ramo `juridico` executa; `financeiro` e `suporte` ficam `skipped` |
| pedido sem categoria conhecida | ramo `default` (`suporte`) |
| pedido urgente | primeiro caso (`escalar`) vence, mesmo sendo juridico |
| classificador com "formato quebrado" | 1 nova tentativa e segue; com "sempre quebrado", no `failed` com os erros |
| queda simulada apos o desvio + `--resume` | mesmo ramo, classificador nao roda de novo, desvio reaproveitado |
| join `all_active` apos ramos alternativos | consolidacao roda uma vez, depois do ramo escolhido |
| join `any` com dois ramos paralelos | no roda uma vez |
| entrada fora do `input_schema` | workflow nao inicia, erros listados |
| `end` com `failed` | workflow `failed` com a mensagem renderizada |
| definicao com destino inexistente | workflow nao inicia, erro com o caminho |
| saida final configurada | `workflow_runs.output` = `output` renderizado |

## 3. Regressao

```bash
dotnet run -- demo all
cd ../.. && tests/run_restart.sh && tests/hitl_restart.sh
```

Esperado: todos os cenarios anteriores (13, 87 checagens) e os dois scripts com `RESULTADO: OK`.

| Medicao | Linha de base (T001, commit `fc8b381`) | Depois da feature (T037) |
|---|---|---|
| Testes de unidade | 99 | 166 |
| Cenarios / checagens | 13 / 87 | 14 / 99 |
| `tests/run_restart.sh` | OK | OK |
| `tests/hitl_restart.sh` | OK | OK |
