# Data Model: Execucao resiliente de workflows

## Banco (tudo idempotente: `add column if not exists`, `drop/add constraint`)

### `workflow_runs`

| Campo | Tipo | Regra |
|---|---|---|
| `status` | check ampliado | `running \| ready \| completed \| failed \| crashed \| cancelled \| expired \| compensation_failed` |
| `owner` | text | instancia dona enquanto `running` |
| `lease_until` | timestamptz | renovado por heartbeat; vencido com `running` = orfa |
| `definition` | jsonb | copia da definicao no inicio (versao fixa) |
| `deadline_at` | timestamptz | `started_at + timeout_seconds` do workflow, quando definido |
| `cancel_requested_at` | timestamptz | pedido de cancelamento |

Indices: `(status, lease_until) where status = 'running'`; `(started_at) where status = 'ready'`.

### `workflow_node_runs`

| Campo | Tipo | Regra |
|---|---|---|
| `agent_run_id` | (existente) | passa a ser gravado no **inicio** do no |
| `compensated_at` | timestamptz | compensacao concluida para este no |

### `runs`

| Campo | Tipo | Regra |
|---|---|---|
| `workflow_node_id` | text | no do workflow que criou o run (contagem de tentativas, auditoria) |

### `workflow_events` (nova)

| Campo | Tipo |
|---|---|
| `id` | bigserial pk |
| `workflow_run_id` | uuid fk `workflow_runs` |
| `type` | text |
| `node_id` | text nulo |
| `data` | jsonb |
| `created_at` | timestamptz default now() |

Indice `(workflow_run_id, id)`.

## Definicao (campos novos)

### Topo

| Campo | Tipo | Regra |
|---|---|---|
| `max_parallel` | inteiro 1–50 | opcional; sem ele, sem limite |
| `timeout_seconds` | inteiro > 0 | opcional; prazo total |

### Por no (qualquer tipo que execute: `agent`, `tool`)

| Campo | Tipo | Regra |
|---|---|---|
| `retry.max_attempts` | 1–10 | padrao 1 (sem nova tentativa) |
| `retry.backoff_seconds` | numero >= 0 | padrao 1 |
| `retry.factor` | numero >= 1 | padrao 2; espera da tentativa n = backoff * factor^(n-1) |
| `retry.on` | lista de `error`, `timeout` | padrao as duas |
| `timeout_seconds` | inteiro > 0 | opcional |
| `optional` | boolean | padrao false |
| `compensate` | objeto `{type: tool\|agent, ...}` sem `next` | opcional |

## Estados

### Execucao

```text
running --(lease vence)--> ready --(despachante)--> running
running --> completed | failed | expired | cancelled | compensation_failed
ready   --(cancelamento)--> cancelled
running --(crashAfter, demo)--> crashed --(--resume)--> running
```

### No (`workflow_node_runs.status`)

`running -> completed | failed | skipped`; `failed` de no `optional` conta como concluido para o escalonador.

### Decisao de retomada de um no `running` (`ResumeDecision`, pura)

| Run do no | Decisao |
|---|---|
| nao existe | executar do zero |
| `completed`/`failed`/`cancelled`/`budget_exceeded` | usar o resultado |
| `running` com lease vencido, ou `ready` | assumir e retomar o run |
| `running` com lease valido | esperar (outra instancia ainda escreve; acontece so com relogio adiantado) |
