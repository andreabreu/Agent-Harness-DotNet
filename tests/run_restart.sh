#!/usr/bin/env bash
# Durabilidade da EXECUCAO com processos de verdade (kill -9), lease curto (RUN_LEASE_SECONDS=4):
#  1. queda durante uma chamada lenta sem efeito (wait_seconds): outra instancia recupera o run (run_recovered),
#     repete a chamada e termina; a conversa nao fica "ocupada";
#  2. queda durante run_shell (com efeito): na retomada o comando NAO roda de novo, aparece o pedido humano e
#     "pular" faz o agente seguir;
#  3. dois processos dispatcher disputando runs orfaos: cada run recuperado uma vez, sem eventos duplicados.
# Uso (com Postgres e o dublê de LLM no ar): tests/run_restart.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="$ROOT/src/AgentPoc/bin/Debug/net8.0/AgentPoc.dll"
PORT="${RUN_TEST_PORT:-5094}"
API="http://localhost:$PORT/api"
LOG="$(mktemp -t run_restart.XXXXXX)"
export RUN_LEASE_SECONDS=4 RUN_HEARTBEAT_SECONDS=1
FAILED=0

json() { python3 -c "import sys, json; d = json.load(sys.stdin); print($1)"; }
db() { docker exec agentpoc-postgres psql -U agents -d agents -tA -c "$1"; }
check() { if [ "$2" = "$3" ]; then echo "   OK    $1"; else echo "   FALHOU $1 (esperado '$3', veio '$2')"; FAILED=1; fi; }

start_serve() {
  (cd "$ROOT" && exec dotnet "$DLL" serve --port "$PORT" >>"$LOG" 2>&1) &
  SERVER_PID=$!
  for _ in $(seq 1 60); do
    if curl -sf "$API/agents" >/dev/null; then
      [ "$(lsof -ti "tcp:$PORT" -sTCP:LISTEN)" = "$SERVER_PID" ] && return 0
      echo "a porta $PORT esta com outro processo, nao com o pid $SERVER_PID"; exit 1
    fi
    sleep 0.5
  done
  echo "servidor nao subiu; log em $LOG"; exit 1
}

kill_hard() {
  kill -9 "$1"; wait "$1" 2>/dev/null || true
  if kill -0 "$1" 2>/dev/null || lsof -ti "tcp:$PORT" >/dev/null; then echo "processo $1 ou porta ainda vivos"; exit 1; fi
}

stop() { kill "$1" 2>/dev/null; wait "$1" 2>/dev/null || true; }

wait_sql() {  # wait_sql <sql que devolve t/f> <descricao> [tentativas]
  for _ in $(seq 1 "${3:-160}"); do [ "$(db "$1")" = "t" ] && return 0; sleep 0.25; done
  echo "timeout esperando: $2"; exit 1
}

new_conversation() {
  local conv
  conv=$(curl -sf -X POST "$API/conversations" -H 'Content-Type: application/json' -d '{"agent":"assistant"}' | json 'd["id"]')
  curl -sf -X POST "$API/conversations/$conv/messages" -H 'Content-Type: application/json' -d "{\"text\":\"$1\"}" >/dev/null
  echo "$conv"
}

started() {  # started <conversa> <ferramenta> -> quantas vezes a ferramenta comecou a executar
  db "select count(*) from run_events e join runs r on r.id = e.run_id
       where r.conversation_id = '$1' and e.type = 'tool_call_started' and e.data->>'name' = '$2'"
}

root_status() { db "select status from runs where conversation_id = '$1' and depth = 0 order by started_at desc limit 1"; }

echo "== 1. queda durante uma chamada lenta sem efeito (wait_seconds) =="
start_serve; A=$SERVER_PID
CONV=$(new_conversation "espere 8 segundos e responda")
wait_sql "select exists (select 1 from run_events e join runs r on r.id = e.run_id where r.conversation_id = '$CONV' and e.type = 'tool_call_started' and e.data->>'name' = 'wait_seconds')" "wait_seconds comecar"
OWNER=$(db "select owner from runs where conversation_id = '$CONV' and depth = 0")
kill_hard "$A"
echo "   processo $A encerrado com kill -9 durante wait_seconds; run: $(root_status "$CONV"), dono $OWNER"
start_serve; B=$SERVER_PID
echo "   servidor novo no ar (pid $B, diferente de $A)"
CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/conversations/$CONV/messages" -H 'Content-Type: application/json' -d '{"text":"oi"}')
echo "   mensagem enviada logo apos subir: HTTP $CODE (o turno anterior ainda anda: recuperacao em curso)"
wait_sql "select status = 'completed' from runs where conversation_id = '$CONV' and depth = 0 order by started_at limit 1" "turno recuperado terminar"
RECOVERED=$(db "select (data->>'previous_owner') || ' -> ' || (data->>'recovered_by') from run_events e join runs r on r.id = e.run_id where r.conversation_id = '$CONV' and e.type = 'run_recovered'")
echo "   run_recovered: $RECOVERED"
check "run_recovered registra a instancia anterior (pid $A)" "$(echo "$RECOVERED" | grep -c -- "-$A-" || true)" "1"
check "quem recuperou foi a instancia nova (pid $B)" "$(echo "$RECOVERED" | grep -c -- "-> .*-$B-" || true)" "1"
check "wait_seconds (sem efeito) foi repetida na retomada" "$(started "$CONV" wait_seconds)" "2"
CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/conversations/$CONV/messages" -H 'Content-Type: application/json' -d '{"text":"oi de novo"}')
check "conversa aceita mensagem nova depois da recuperacao (nao fica ocupada)" "$CODE" "202"
wait_sql "select status = 'completed' from runs where conversation_id = '$CONV' and depth = 0 order by started_at desc limit 1" "turno novo terminar"
stop "$B"

echo "== 2. queda durante run_shell (com efeito) =="
rm -f "$ROOT/workspace/efeito_lento.txt"
start_serve; A=$SERVER_PID
CONV=$(new_conversation "rode o comando lento")
wait_sql "select exists (select 1 from approvals where conversation_id = '$CONV' and status = 'pending' and tool = 'run_shell')" "pedido de aprovacao do run_shell"
REQ=$(db "select id from approvals where conversation_id = '$CONV' and status = 'pending'")
curl -sf -X POST "$API/approvals/$REQ" -H 'Content-Type: application/json' -d '{"approved":true}' >/dev/null
wait_sql "select exists (select 1 from run_events e join runs r on r.id = e.run_id where r.conversation_id = '$CONV' and e.type = 'tool_call_started' and e.data->>'name' = 'run_shell')" "run_shell comecar"
KILLED_AT=$(date +%s)
kill_hard "$A"
echo "   processo $A encerrado com kill -9 durante 'sleep 6 && echo executado >> efeito_lento.txt'"
start_serve; B=$SERVER_PID
echo "   servidor novo no ar (pid $B, diferente de $A)"
wait_sql "select exists (select 1 from approvals where conversation_id = '$CONV' and status = 'pending' and kind = 'question')" "pergunta de retomada" 200
echo "   pedido humano: $(db "select question from approvals where conversation_id = '$CONV' and status = 'pending'")"
REQ=$(db "select id from approvals where conversation_id = '$CONV' and status = 'pending'")
curl -sf -X POST "$API/approvals/$REQ" -H 'Content-Type: application/json' -d '{"answer":"pular"}' >/dev/null
wait_sql "select status = 'completed' from runs where conversation_id = '$CONV' and depth = 0" "turno terminar"
echo "   resposta final: $(db "select output from runs where conversation_id = '$CONV' and depth = 0" | cut -c1-150)"
while [ $(( $(date +%s) - KILLED_AT )) -lt 9 ]; do sleep 1; done
check "run_shell comecou uma unica vez (nao foi repetido)" "$(started "$CONV" run_shell)" "1"
check "efeito no disco aconteceu no maximo uma vez (o shell orfao do processo morto)" "$(wc -l < "$ROOT/workspace/efeito_lento.txt" 2>/dev/null | tr -d ' ' || echo 0)" "1"
check "o agente seguiu depois de 'pular'" "$(db "select output like '%PULADO%' from runs where conversation_id = '$CONV' and depth = 0")" "t"
stop "$B"

echo "== 3. dois processos dispatcher disputando runs orfaos =="
start_serve; A=$SERVER_PID
CONVS=()
for i in 1 2 3; do CONVS+=("$(new_conversation "espere 10 segundos e responda")"); done
IN_LIST=$(printf "'%s'," "${CONVS[@]}"); IN_LIST="${IN_LIST%,}"
wait_sql "select count(distinct r.conversation_id) = 3 from run_events e join runs r on r.id = e.run_id where r.conversation_id in ($IN_LIST) and e.type = 'tool_call_started'" "3 runs executando"
kill_hard "$A"
echo "   processo $A encerrado com kill -9 com 3 runs executando"
(cd "$ROOT" && exec dotnet "$DLL" dispatcher >>"$LOG" 2>&1) & D1=$!
(cd "$ROOT" && exec dotnet "$DLL" dispatcher >>"$LOG" 2>&1) & D2=$!
echo "   dispatchers em processos separados: pid $D1 e pid $D2"
wait_sql "select count(*) = 3 from runs where conversation_id in ($IN_LIST) and depth = 0 and status = 'completed'" "3 runs terminarem" 240
echo "   recuperacoes:"
db "select '     ' || left(r.conversation_id::text, 8) || ': ' || (e.data->>'previous_owner') || ' -> ' || (e.data->>'recovered_by')
      from run_events e join runs r on r.id = e.run_id where r.conversation_id in ($IN_LIST) and e.type = 'run_recovered' order by e.id"
check "cada run foi recuperado exatamente uma vez" "$(db "select count(*) from run_events e join runs r on r.id = e.run_id where r.conversation_id in ($IN_LIST) and e.type = 'run_recovered'")" "3"
check "cada run foi retomado exatamente uma vez" "$(db "select count(*) from run_events e join runs r on r.id = e.run_id where r.conversation_id in ($IN_LIST) and e.type = 'run_resumed'")" "3"
check "nenhum evento duplicado (run_id, seq)" "$(db "select count(*) from (select e.run_id, e.seq from run_events e join runs r on r.id = e.run_id where r.conversation_id in ($IN_LIST) group by e.run_id, e.seq having count(*) > 1) d")" "0"
echo "   processos que recuperaram: $(db "select string_agg(distinct substring(e.data->>'recovered_by' from '-([0-9]+)-'), ', ') from run_events e join runs r on r.id = e.run_id where r.conversation_id in ($IN_LIST) and e.type = 'run_recovered'")"
stop "$D1"; stop "$D2"

if [ "$FAILED" = 0 ]; then echo "RESULTADO: OK"; else echo "RESULTADO: FALHOU (log em $LOG)"; exit 1; fi
