#!/usr/bin/env bash
# HITL duravel com o servidor derrubado: o subagente pergunta, o processo e encerrado com kill -9 com a pergunta
# pendente, um processo novo sobe, a resposta e gravada pela API e o despachante do processo novo termina a cadeia.
# Uso (com Postgres e o dublê de LLM no ar): tests/hitl_restart.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="$ROOT/src/AgentPoc/bin/Debug/net8.0/AgentPoc.dll"
PORT="${HITL_TEST_PORT:-5091}"
API="http://localhost:$PORT/api"
LOG="$(mktemp -t hitl_restart.XXXXXX)"
json() { python3 -c "import sys, json; d = json.load(sys.stdin); print($1)"; }

start_server() {
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

wait_for() {  # wait_for <expressao python sobre d> <descricao>
  for _ in $(seq 1 120); do
    if curl -sf "$API/conversations/$CONV" | python3 -c "import sys, json; d = json.load(sys.stdin); sys.exit(0 if ($1) else 1)"; then return 0; fi
    sleep 0.25
  done
  echo "timeout esperando: $2"; exit 1
}

db() { docker exec agentpoc-postgres psql -U agents -d agents -tA -c "$1"; }

start_server
echo "1. servidor no ar (pid $SERVER_PID)"
CONV=$(curl -sf -X POST "$API/conversations" -H 'Content-Type: application/json' -d '{"agent":"orchestrator"}' | json 'd["id"]')
curl -sf -X POST "$API/conversations/$CONV/messages" -H 'Content-Type: application/json' \
  -d '{"text":"Pergunte a pessoa, via especialista, qual ambiente usar no deploy."}' >/dev/null
wait_for 'len(d["pending_approvals"]) == 1' "pergunta pendente"
REQ=$(curl -sf "$API/conversations/$CONV" | json 'd["pending_approvals"][0]["approval_id"]')
echo "2. pergunta pendente do subagente: $(curl -sf "$API/conversations/$CONV" | json 'd["pending_approvals"][0]["question"]')"

kill -9 "$SERVER_PID"; wait "$SERVER_PID" 2>/dev/null || true
if kill -0 "$SERVER_PID" 2>/dev/null || lsof -ti "tcp:$PORT" >/dev/null; then echo "processo ou porta ainda vivos"; exit 1; fi
FIRST_PID=$SERVER_PID
echo "3. processo $SERVER_PID encerrado com kill -9. Estado no banco:"
db "select '   ' || agent_id || ' depth=' || depth || ' status=' || status from runs where conversation_id = '$CONV' order by depth"

start_server
echo "4. servidor novo no ar (pid $SERVER_PID, diferente de $FIRST_PID); gravando a resposta pela API"
curl -sf -X POST "$API/approvals/$REQ" -H 'Content-Type: application/json' -d '{"answer":"producao"}' | sed 's/^/   decisao: /'; echo
wait_for 'd["root_status"] == "completed"' "cadeia concluida"
echo "5. cadeia concluida. Estado final:"
db "select '   ' || agent_id || ' depth=' || depth || ' status=' || status || ' retomado por: ' || coalesce((select string_agg(e.data->>'by', ', ') from run_events e where e.run_id = r.id and e.type = 'run_resumed'), '-') from runs r where conversation_id = '$CONV' order by depth"
FINAL=$(curl -sf "$API/conversations/$CONV" | json '[m["content"] for m in d["messages"] if m["role"] == "assistant" and m["content"]][-1]')
echo "   resposta final do pai: $FINAL"
kill "$SERVER_PID"; wait "$SERVER_PID" 2>/dev/null || true

case "$FINAL" in
  *producao*) echo "RESULTADO: OK, o pai terminou com a resposta do filho depois do restart" ;;
  *) echo "RESULTADO: FALHOU"; exit 1 ;;
esac
