#!/usr/bin/env bash
# Durabilidade de WORKFLOWS com processos de verdade (kill -9), lease curto (RUN_LEASE_SECONDS=4):
#  1. queda durante o agente de um no: um dispatcher em outro processo recupera a execucao (workflow_recovered),
#     assume o MESMO run do agente, continua do historico e o workflow termina;
#  2. queda durante run_shell num no tool (com efeito): na retomada o comando NAO roda de novo (sem ninguem para
#     responder, a decisao e pular) e o workflow termina;
#  3. dois processos dispatcher disputando tres execucoes orfas: cada uma recuperada e concluida uma vez.
# Uso (com Postgres e o dublê de LLM no ar): tests/workflow_restart.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="$ROOT/src/AgentPoc/bin/Debug/net8.0/AgentPoc.dll"
LOG="$(mktemp -t workflow_restart.XXXXXX)"
export RUN_LEASE_SECONDS=4 RUN_HEARTBEAT_SECONDS=1
FAILED=0

db() { docker exec agentpoc-postgres psql -U agents -d agents -tA -c "$1"; }
check() { if [ "$2" = "$3" ]; then echo "   OK    $1"; else echo "   FALHOU $1 (esperado '$3', veio '$2')"; FAILED=1; fi; }

wait_sql() {  # wait_sql <sql que devolve t/f> <descricao> [tentativas]
  for _ in $(seq 1 "${3:-160}"); do [ "$(db "$1")" = "t" ] && return 0; sleep 0.25; done
  echo "timeout esperando: $2 (log em $LOG)"; exit 1
}

kill_hard() { kill -9 "$1"; wait "$1" 2>/dev/null || true; }
stop() { kill "$1" 2>/dev/null; wait "$1" 2>/dev/null || true; }

save_workflow() {  # save_workflow <id> <definicao json>
  db "insert into workflows (id, description, definition) values ('$1', 'teste de queda', '$2')
      on conflict (id) do update set definition = excluded.definition, enabled = true" >/dev/null
}

start_workflow() {  # start_workflow <id> <entrada> [opcoes...] -> pid em WF_PID
  local id="$1" input="$2"; shift 2
  (cd "$ROOT" && exec dotnet "$DLL" workflow "$id" "$input" "$@" >>"$LOG" 2>&1) &
  WF_PID=$!
}

last_run() { db "select id from workflow_runs where workflow_id = '$1' order by started_at desc limit 1"; }

started() {  # started <workflow run> <ferramenta> -> quantas vezes a ferramenta comecou a executar na execucao
  db "select count(*) from run_events e join runs r on r.id = e.run_id
       where r.workflow_run_id = '$1' and e.type = 'tool_call_started' and e.data->>'name' = '$2'"
}

recovered() { db "select (data->>'previous_owner') || ' -> ' || (data->>'recovered_by') from workflow_events where workflow_run_id = '$1' and type = 'workflow_recovered'"; }

save_workflow teste_queda_agente '{"nodes": {"esperar": {"type": "agent", "agent": "assistant", "input": "{{input}}"}}}'
save_workflow teste_queda_shell '{"nodes": {"reservar": {"type": "tool", "tool": "run_shell", "args": {"command": "sleep 6 && echo executado >> efeito_workflow.txt"}}}}'

echo "== 1. queda durante o agente de um no =="
start_workflow teste_queda_agente "espere 8 segundos e responda"; A=$WF_PID
wait_sql "select exists (select 1 from run_events e join runs r on r.id = e.run_id join workflow_runs w on w.id = r.workflow_run_id
           where w.workflow_id = 'teste_queda_agente' and w.status = 'running' and e.type = 'tool_call_started' and e.data->>'name' = 'wait_seconds')" "wait_seconds comecar"
WF=$(last_run teste_queda_agente)
AGENT_RUN=$(db "select id from runs where workflow_run_id = '$WF' and workflow_node_id = 'esperar'")
kill_hard "$A"
echo "   processo $A encerrado com kill -9 durante wait_seconds; execucao $WF, run do agente $AGENT_RUN"
(cd "$ROOT" && exec dotnet "$DLL" dispatcher >>"$LOG" 2>&1) & D=$!
echo "   dispatcher em outro processo (pid $D)"
wait_sql "select status = 'completed' from workflow_runs where id = '$WF'" "execucao recuperada terminar" 240
RECOVERED=$(recovered "$WF")
echo "   workflow_recovered: $RECOVERED"
check "workflow_recovered registra a instancia anterior (pid $A)" "$(echo "$RECOVERED" | grep -c -- "-$A-" || true)" "1"
check "quem recuperou foi o dispatcher (pid $D)" "$(echo "$RECOVERED" | grep -c -- "-> .*-$D-" || true)" "1"
check "o no terminou com o MESMO run do agente" "$(db "select agent_run_id from workflow_node_runs where workflow_run_id = '$WF' and node_id = 'esperar'")" "$AGENT_RUN"
check "nenhum run novo foi criado para o no" "$(db "select count(*) from runs where workflow_run_id = '$WF' and workflow_node_id = 'esperar'")" "1"
check "o run do agente foi retomado do historico" "$(db "select count(*) from run_events where run_id = '$AGENT_RUN' and type = 'run_resumed'")" "1"
check "o run do agente terminou completed" "$(db "select status from runs where id = '$AGENT_RUN'")" "completed"
stop "$D"

echo "== 2. queda durante run_shell num no tool (com efeito) =="
rm -f "$ROOT/workspace/efeito_workflow.txt"
start_workflow teste_queda_shell "reservar" --approval auto; A=$WF_PID
wait_sql "select exists (select 1 from run_events e join runs r on r.id = e.run_id join workflow_runs w on w.id = r.workflow_run_id
           where w.workflow_id = 'teste_queda_shell' and w.status = 'running' and e.type = 'tool_call_started' and e.data->>'name' = 'run_shell')" "run_shell comecar"
WF=$(last_run teste_queda_shell)
KILLED_AT=$(date +%s)
kill_hard "$A"
echo "   processo $A encerrado com kill -9 durante 'sleep 6 && echo executado >> efeito_workflow.txt'"
(cd "$ROOT" && exec dotnet "$DLL" dispatcher >>"$LOG" 2>&1) & D=$!
wait_sql "select status = 'completed' from workflow_runs where id = '$WF'" "execucao recuperada terminar" 240
while [ $(( $(date +%s) - KILLED_AT )) -lt 9 ]; do sleep 1; done
echo "   saida do no: $(db "select left(output, 120) from workflow_node_runs where workflow_run_id = '$WF' and node_id = 'reservar'")"
check "workflow_recovered registrado" "$(db "select count(*) from workflow_events where workflow_run_id = '$WF' and type = 'workflow_recovered'")" "1"
check "run_shell comecou uma unica vez (nao foi repetido)" "$(started "$WF" run_shell)" "1"
check "tool_call_recovered decidiu pular" "$(db "select string_agg(e.data->>'decision', ',') from run_events e join runs r on r.id = e.run_id where r.workflow_run_id = '$WF' and e.type = 'tool_call_recovered'")" "Skip"
check "efeito no disco aconteceu no maximo uma vez (o shell orfao do processo morto)" "$(wc -l < "$ROOT/workspace/efeito_workflow.txt" 2>/dev/null | tr -d ' ' || echo 0)" "1"
stop "$D"

echo "== 3. dois processos dispatcher disputando tres execucoes orfas =="
PIDS=()
for i in 1 2 3; do start_workflow teste_queda_agente "espere 10 segundos e responda ($i)"; PIDS+=("$WF_PID"); done
wait_sql "select count(distinct w.id) = 3 from run_events e join runs r on r.id = e.run_id join workflow_runs w on w.id = r.workflow_run_id
           where w.workflow_id = 'teste_queda_agente' and w.status = 'running' and e.type = 'tool_call_started' and e.data->>'name' = 'wait_seconds'" "3 execucoes no agente"
WFS=$(db "select string_agg(quote_literal(id::text), ',') from workflow_runs where workflow_id = 'teste_queda_agente' and status = 'running'")
for p in "${PIDS[@]}"; do kill_hard "$p"; done
echo "   processos ${PIDS[*]} encerrados com kill -9 com 3 execucoes no meio"
(cd "$ROOT" && exec dotnet "$DLL" dispatcher >>"$LOG" 2>&1) & D1=$!
(cd "$ROOT" && exec dotnet "$DLL" dispatcher >>"$LOG" 2>&1) & D2=$!
echo "   dispatchers em processos separados: pid $D1 e pid $D2"
wait_sql "select count(*) = 3 from workflow_runs where id in ($WFS) and status = 'completed'" "3 execucoes terminarem" 320
echo "   recuperacoes:"
db "select '     ' || left(workflow_run_id::text, 8) || ': ' || (data->>'previous_owner') || ' -> ' || (data->>'recovered_by')
      from workflow_events where workflow_run_id in ($WFS) and type = 'workflow_recovered' order by id"
check "cada execucao foi recuperada exatamente uma vez" "$(db "select count(*) from workflow_events where workflow_run_id in ($WFS) and type = 'workflow_recovered'")" "3"
check "cada run de agente foi retomado exatamente uma vez" "$(db "select count(*) from run_events e join runs r on r.id = e.run_id where r.workflow_run_id in ($WFS) and e.type = 'run_resumed'")" "3"
check "nenhum run novo para os nos" "$(db "select count(*) from runs where workflow_run_id in ($WFS)")" "3"
echo "   processos que recuperaram: $(db "select string_agg(distinct substring(data->>'recovered_by' from '-([0-9]+)-'), ', ') from workflow_events where workflow_run_id in ($WFS) and type = 'workflow_recovered'")"
stop "$D1"; stop "$D2"

if [ "$FAILED" = 0 ]; then echo "RESULTADO: OK"; else echo "RESULTADO: FALHOU (log em $LOG)"; exit 1; fi
