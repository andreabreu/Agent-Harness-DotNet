# Research: Execucao resiliente de workflows

Levantamento no codigo em 2026-11-08 (commit `8146fb8`).

## Estado atual

- `WorkflowEngine.RunAsync` roda no processo de quem chamou (CLI `workflow`, demo). Se o processo morre, a execucao
  fica `running` para sempre; a retomada e manual (`workflow --resume <id>`) e reaproveita `completed`/`skipped`.
- A retomada le a definicao atual de `workflows` (nao ha copia por execucao); o mesmo vale para `HooksForRunAsync`
  (regras da 001 nas tarefas da fila).
- Runs de agente ja tem `owner`/`lease_until` com heartbeat (`RunLeaseKeeper`) e historico em `run_messages`
  quando nao sao de conversa; `AgentRunner.ResumeAsync` retoma um run do historico. A recuperacao de orfaos
  (`RunChain.RecoverOrphansAsync`) so pega runs com `conversation_id`.
- No `agent`: o `runs.id` so e gravado em `workflow_node_runs.agent_run_id` quando o no termina; numa queda nao ha
  como achar o run interrompido.
- No `tool`: `RunToolAsync` cria um pseudo-run e chama `ToolExecutor.ExecuteAsync`, que grava `tool_call_started`;
  `ToolExecutor.ExecutePendingAsync` ja decide repetir/perguntar/pular para chamadas interrompidas.
- `ResumeDispatcher` pega runs `ready` com `FOR UPDATE SKIP LOCKED`, acorda por `LISTEN/NOTIFY` e varre (expiracao,
  orfaos, reconciliacao).
- `run_shell` respeita o `CancellationToken` (mata a arvore de processos); `HttpTool` e MCP tambem recebem o token.
- `workflow_runs.status`: `running | completed | failed | crashed`. Nao ha eventos de workflow; so `run_events`.

## Decisoes

### D1. Lease por execucao de workflow, reaproveitando o mecanismo dos runs

- **Decision**: `workflow_runs` ganha `owner` e `lease_until`. O `RunLeaseKeeper` passa a receber o comando de
  renovacao (tabela `runs` ou `workflow_runs`) em vez de ter o SQL fixo; o motor mantem o lease enquanto executa e
  para de escrever ao perder a posse (`LeaseLostException`). Mesmos `RUN_LEASE_SECONDS`/`RUN_HEARTBEAT_SECONDS`.
- **Rationale**: Principio I e V — um mecanismo, testado pelos scripts de queda.
- **Alternatives considered**: lease separado com tabela propria — duplica codigo; advisory locks do Postgres —
  morrem com a conexao, mas nao deixam rastro auditavel de quem era o dono.

### D2. O despachante retoma workflows

- **Decision**: interface `IWorkflowRecovery` no Harness, implementada pelo `WorkflowEngine`:
  `RecoverOrphansAsync(by)` (execucoes `running` com lease vencido viram `ready` e ganham o evento
  `workflow_recovered` com dono anterior e novo) e `ResumeNextAsync(owner, ct)` (pega uma `ready` com
  `FOR UPDATE SKIP LOCKED`, assume o lease e continua). O `ResumeDispatcher` chama os dois na varredura e no laco,
  com um limite proprio de execucoes de workflow simultaneas. `NOTIFY` no mesmo canal `agent_run_ready`.
- **Rationale**: as instancias que ja rodam o despachante (`serve`, `chat`, `dispatcher`) passam a cobrir workflows;
  duas instancias nunca retomam a mesma execucao.

### D3. Retomar o agente de um no do ponto onde parou

- **Decision**: o motor gera o id do run antes de chamar o agente (`RunAsync` ja aceita `runId`) e grava
  `agent_run_id` no inicio do no. `runs` ganha `workflow_node_id`. Na retomada, um no `running`:
  - run terminado → usa o resultado (queda entre o fim do agente e o checkpoint);
  - run `running` com lease vencido → assume o run (`owner`, `lease_until`) e chama `ResumeAsync` com o gate e as
    regras da execucao;
  - run inexistente → executa o no do zero.
  Nos com `output_schema`: a tentativa atual e retomada; as tentativas restantes contam os runs ja feitos para o no.
- **Rationale**: FR-002; reaproveita `ResumeAsync` e o historico ja gravado.

### D4. No `tool` com efeito interrompido

- **Decision**: `RunToolAsync` tambem recebe um id de run e um id de chamada estaveis (`wf_<no>_<tentativa>`). Na
  retomada, se o pseudo-run tem `tool_call_started` sem `tool_result`, o motor chama
  `ToolExecutor.ExecutePendingAsync`: sem efeito repete; com efeito pergunta pelo gate da execucao.
- **Limite ate a 004**: o gate de workflow (`ModeHumanGate`) nao tem pessoa, entao a pergunta volta "SEM RESPOSTA"
  e a decisao e **pular** (o mais seguro, Principio II), registrada em `tool_call_recovered`. No modo `ask` o
  terminal pergunta. A espera duravel vem com a 004.

### D5. Versao fixa por execucao

- **Decision**: `workflow_runs.definition jsonb` recebe a definicao no inicio; retomada, `HooksForRunAsync` e o
  despachante usam essa copia. Execucoes antigas (sem copia) usam a definicao atual e geram o evento
  `definition_not_pinned`.
- **Rationale**: FR-004; resolve tambem o limite registrado na 001 (regras da definicao atual no `--resume`).
- **Alternatives considered**: tabela `workflow_versions` com historico — e o certo para a 009 (rascunho,
  publicacao, diff); a copia por execucao continua valida depois dela.

### D6. Tentativas e prazos por no

- **Decision**: no ganha `retry` (`max_attempts` 1–10, padrao 1; `backoff_seconds` padrao 1; `factor` padrao 2;
  `on`: lista de `error` e `timeout`, padrao as duas) e `timeout_seconds`. O workflow ganha `timeout_seconds`.
  - Prazo do no: `CancellationTokenSource` ligado ao do workflow; ao vencer, o run do agente/ferramenta e cancelado e
    o no falha com `timeout` (elegivel a nova tentativa).
  - Prazo do workflow: guardado como `deadline_at` em `workflow_runs` (sobrevive a retomada); ao vencer, nos em
    execucao sao cancelados e a execucao termina `expired`.
  - Espera entre tentativas: em memoria (`Task.Delay`); numa queda durante a espera, a retomada recomeca o no. A
    espera duravel e trabalho da 006 (`wait`).
  - No `tool` de ferramenta com efeito so repete sozinho se a tentativa que falhou nao chegou a `tool_call_started`
    (ex.: negada pela politica, argumento invalido); senao falha (Principio II). No `agent` pode repetir: as
    ferramentas do agente continuam governadas pelas politicas.
- **Eventos**: `node_retry` (tentativa, motivo, espera), `node_timeout`.

### D7. No opcional

- **Decision**: `optional: true`. Falha definitiva: `workflow_node_runs.status = failed`, mas para o escalonador o no
  conta como concluido (arestas ativas) e a saida e a mensagem de erro. Evento `node_failed_optional`.

### D8. Cancelamento

- **Decision**: comando `workflow --cancel <id>`: grava `cancel_requested_at` e envia `NOTIFY agent_run_cancel`.
  - Dono vivo: o heartbeat le o pedido e cancela o token; runs dos nos terminam `cancelled`; nada mais e agendado;
    status `cancelled`.
  - Sem dono vivo (orfa) ou `ready`: a varredura marca `cancelled` direto e cancela os runs dos nos com lease vencido
    (mesma regra de `CancelTreeAsync`).
- **Rationale**: FR-008; a mesma propagacao que o chat ja tem.

### D9. Limite de paralelismo

- **Decision**: `max_parallel` no topo da definicao. `WorkflowScheduler.Next(limit)` libera ate `limit - em execucao`
  nos, na ordem da definicao; os demais continuam pendentes.

### D10. Compensacao

- **Decision**: no pode ter `compensate` (um no `tool` ou `agent` embutido, sem `next`). Quando o workflow falha,
  os nos concluidos com `compensate` rodam a compensacao em ordem inversa de conclusao; `workflow_node_runs.compensated_at`
  marca cada uma (retomavel). Status final `failed` (todas ok) ou `compensation_failed`. Eventos
  `compensation_started`, `node_compensated`, `compensation_failed`.

### D11. Eventos de workflow

- **Decision**: tabela `workflow_events (id bigserial, workflow_run_id, type, node_id, data jsonb, created_at)`.
  Tipos desta feature: `workflow_recovered`, `definition_not_pinned`, `node_retry`, `node_timeout`,
  `node_failed_optional`, `workflow_cancelled`, `workflow_expired`, `compensation_started`, `node_compensated`,
  `compensation_failed`.
- **Rationale**: FR-011; base para a linha do tempo e o SSE da 008.

### D12. Prova

- Unidade: escalonador com limite e no opcional; politica de tentativa (elegibilidade, espera com fator, efeito);
  validacao dos campos novos; decisao de retomada de no (`ResumeDecision` pura: run terminado / orfao / ausente).
- Demo `workflow_resiliente`: falha transitoria que passa na 3a tentativa, prazo de no, no opcional, prazo do
  workflow, limite de paralelismo, cancelamento, compensacao em ordem inversa, versao fixa (definicao alterada
  durante a execucao) e retomada pelo despachante de uma execucao com lease vencido (forjada no banco).
- Script `tests/workflow_restart.sh` com `kill -9`: (1) queda durante o agente de um no, outra instancia
  (`dispatcher`) retoma o mesmo run e conclui; (2) queda durante `run_shell` num no `tool`: nao repete;
  (3) dois `dispatcher` disputando tres execucoes orfas: cada uma retomada uma vez.
- Dublê: endpoint `/fake/flaky?key=K&until=N` (503 ate a N-esima chamada) para as tentativas e o ramo "espere N
  segundos" ja existente para os prazos.

## Sem pendencias

Nenhum NEEDS CLARIFICATION; nenhuma dependencia nova.
