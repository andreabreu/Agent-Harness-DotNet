# Quickstart: validar aprovacao segura em workflows

## Pre-requisitos

```bash
docker compose up -d
python3 tests/mock_llm.py &
export AZURE_FOUNDRY_ENDPOINT=http://localhost:8765/openai/v1 AZURE_FOUNDRY_API_KEY=teste AZURE_FOUNDRY_DEPLOYMENT=mock
export SEARXNG_URL=http://localhost:8765/fake
unset APPROVAL_MODE
cd src/AgentPoc && dotnet run -- setup
```

## 1. Testes de unidade

```bash
dotnet test tests/AgentPoc.Tests
```

Esperado: os 74 atuais e os novos de precedencia (`ToolPolicy` com regras), `ModeHumanGate.For` e validacao
de padroes passam.

## 2. Cenario de ponta a ponta

```bash
dotnet run -- demo workflow_aprovacao
```

Esperado (cada item e uma checagem do cenario):

| Situacao | Resultado |
|---|---|
| no `tool` com `run_shell`, sem modo | negado, `source = mode_default`, nenhum `tool_call_started` |
| mesmo workflow com modo `auto` explicito | executa, `source = mode_explicit` |
| `allow: ["run_shell"]`, sem modo | executa, `source = workflow_allow` |
| `block: ["kb_search"]` (global `auto`) | bloqueado, `source = workflow_block` |
| `allow: ["run_shell"]` com `sudo ls` | bloqueado, `source = deny_when` |
| no `agent` (orquestrador) delegando `ls -la` ao coder, sem modo | o `run_shell` do subagente e negado (`mode_default`) |
| no `agent` criando tarefa na fila para o coder, worker com modo global `auto` | o `run_shell` da tarefa e negado (`mode_default`) |
| regra com padrao inexistente | workflow nao inicia, mensagem cita o padrao |

## 3. Regressao

```bash
dotnet run -- demo all
cd ../.. && tests/run_restart.sh && tests/hitl_restart.sh
```

Esperado: todos os cenarios anteriores e os dois scripts com `RESULTADO: OK`.

| Medicao | Linha de base (T001, commit `c45febb`) | Depois da feature (T028) |
|---|---|---|
| Testes de unidade | 74 | 99 |
| Cenarios / checagens | 12 / 77 | 13 / 87 (com a correcao da fila) |
| `tests/run_restart.sh` | OK | OK |
| `tests/hitl_restart.sh` | OK | OK |

## 4. Manual

```bash
dotnet run -- workflow relatorio_tecnico "teste"                   # log: aprovacao: deny (padrao de workflow)
dotnet run -- workflow relatorio_tecnico "teste" --approval auto   # log: aprovacao: auto (explicito)
```
