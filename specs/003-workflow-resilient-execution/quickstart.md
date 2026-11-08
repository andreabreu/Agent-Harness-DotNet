# Quickstart: validar execucao resiliente

## Pre-requisitos

Os mesmos da 001 ([quickstart](../001-workflow-safe-approval/quickstart.md#pre-requisitos)), com o dublê
reiniciado depois de atualizar `tests/mock_llm.py`.

## 1. Testes de unidade

```bash
dotnet test tests/AgentPoc.Tests
```

Esperado: os 166 atuais e os novos (escalonador com limite e opcional, politica de tentativa, decisao de retomada,
validacao dos campos novos).

## 2. Cenario de ponta a ponta

```bash
cd src/AgentPoc && dotnet run -- demo workflow_resiliente
```

| Situacao | Esperado |
|---|---|
| no que falha 2x e passa na 3a | 3 tentativas, 2 eventos `node_retry` com espera crescente, workflow `completed` |
| no com prazo de 2 s e agente que demora 5 s | `node_timeout`, no `failed`, run do agente `cancelled` |
| no opcional que falha | `node_failed_optional`; workflow `completed`; no seguinte executa |
| workflow com prazo total vencido | `expired`, `workflow_expired`, nos em execucao cancelados |
| `max_parallel: 2` com 4 ramos prontos | nunca mais de 2 nos `running` ao mesmo tempo |
| cancelamento durante a execucao | `cancelled`, `workflow_cancelled`, runs dos nos `cancelled`, nada novo iniciado |
| A e B com compensacao, C falha | compensa B, depois A; `node_compensated` x2; workflow `failed` |
| definicao alterada no meio + retomada | a execucao termina com a versao antiga; uma nova usa a nova |
| execucao com lease vencido (forjado) | despachante registra `workflow_recovered` e conclui; nos concluidos nao rodam de novo |

## 3. Queda real

```bash
cd ../.. && tests/workflow_restart.sh
```

| Parte | Esperado |
|---|---|
| `kill -9` durante o agente de um no | outra instancia retoma o MESMO run do agente; `workflow_recovered` com pids diferentes |
| `kill -9` durante `run_shell` num no `tool` | o comando nao roda de novo; `tool_call_recovered` = pular |
| dois `dispatcher` e tres execucoes orfas | cada execucao recuperada e concluida uma vez |

## 4. Regressao

```bash
cd src/AgentPoc && dotnet run -- demo all
cd ../.. && tests/run_restart.sh && tests/hitl_restart.sh
```

Esperado: 14 cenarios anteriores (99 checagens) e os scripts com `RESULTADO: OK`.
