#!/usr/bin/env python3
"""
Dublê de LLM compatível com OpenAI Chat Completions (mesmo formato do Azure Foundry).

NAO e um modelo: decide as tool calls por regras fixas, por agente, so para testar a
infraestrutura da POC (loop, discovery, delegacao, politicas, budget, workflow, fila,
evidencias) sem chave de API e sem custo.

Uso:
    python3 tests/mock_llm.py            # escuta em http://localhost:8765
    AZURE_FOUNDRY_ENDPOINT=http://localhost:8765/openai/v1 \
    AZURE_FOUNDRY_API_KEY=teste AZURE_FOUNDRY_DEPLOYMENT=mock dotnet run -- demo all

Sem internet, aponte as ferramentas HTTP para as rotas falsas deste servidor (so no banco, sem codigo):
    psql ... -f tests/offline_http_tools.sql
"""
import json
import os
import re
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(os.environ.get("MOCK_LLM_PORT", "8765"))
LATENCY = float(os.environ.get("MOCK_LLM_LATENCY", "0.3"))


def call(name, **args):
    return (name, args)


class Conversation:
    def __init__(self, body):
        self.body = body
        msgs = body.get("messages", [])
        self.system = next((m.get("content") or "" for m in msgs if m["role"] == "system"), "")
        self.task = next((m.get("content") or "" for m in msgs if m["role"] == "user"), "")
        self.available = {t["function"]["name"] for t in body.get("tools", [])}
        self.history = []  # (name, args, result)
        self.turn = []  # so as chamadas depois da ultima mensagem do usuario (turno atual do chat)
        self.last_user = ""
        self.assistant_texts = []
        pending = {}
        for m in msgs:
            if m["role"] == "user":
                self.last_user = m.get("content") or ""
                self.turn = []
            elif m["role"] == "assistant":
                if m.get("content"):
                    self.assistant_texts.append(m["content"])
                for tc in m.get("tool_calls") or []:
                    args = json.loads(tc["function"]["arguments"] or "{}")
                    pending[tc["id"]] = (tc["function"]["name"], args)
            elif m["role"] == "tool":
                name, args = pending.get(m.get("tool_call_id"), ("?", {}))
                self.history.append((name, args, m.get("content") or ""))
                self.turn.append((name, args, m.get("content") or ""))

    def turn_called(self, name):
        return sum(1 for h in self.turn if h[0] == name)

    def turn_result(self, name):
        for h in reversed(self.turn):
            if h[0] == name:
                return h[2]
        return ""

    def called(self, name):
        return any(h[0] == name for h in self.history)

    def result(self, name):
        for h in reversed(self.history):
            if h[0] == name:
                return h[2]
        return ""

    def searched(self, fragment):
        return any(h[0] == "search_tools" and fragment in h[1].get("query", "") for h in self.history)


def need_tool(c, tool, query):
    """Se a ferramenta nao esta carregada, pede ao harness para descobri-la."""
    if tool in c.available:
        return None
    if not c.searched(query):
        return [call("search_tools", query=query)]
    return None


# ----------------------------------------------------------------------------- agentes

def orchestrator(c):
    t = c.task.lower()
    if "delegue ao coder" in t:
        if not c.called("delegate_task"):
            return [call("delegate_task", agent="coder", task="Rode ls -la no shell e apague o arquivo nada.txt.")]
        return "Resultado do coder: " + c.result("delegate_task")

    if "pergunte a pessoa" in t:
        if not c.called("delegate_task"):
            return [call("delegate_task", agent="researcher",
                         task="Pergunte a pessoa qual ambiente usar no deploy (homologacao ou producao) e devolva a escolha.")]
        return "Resultado do especialista: " + c.result("delegate_task")

    if "create_task" in t:
        if not c.called("create_task"):
            calls = []
            for letter, agent, text in re.findall(r"\((\w)\) para (\w+): ([^\n]+)", c.task):
                calls.append(call("create_task", agent=agent, task=text.strip().rstrip(";.")))
            return calls
        ids = re.findall(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                         " ".join(h[2] for h in c.history))
        return "Tarefas criadas na fila: " + ", ".join(ids)

    if "mcp" in t:
        mcp_tools = sorted(n for n in c.available if n.startswith("mcp_"))
        if not mcp_tools and not c.searched("mcp"):
            return [call("search_tools", query="mcp eco texto")]
        echo = next((n for n in mcp_tools if n.endswith("echo")), None)
        if echo and not c.called(echo):
            return [call(echo, texto="ola mcp")]
        return "Resultado MCP: " + (c.result(echo) if echo else "nenhuma ferramenta MCP encontrada")

    if "pesquise a fundo" in t:
        if not c.called("list_agents"):
            return [call("list_agents")]
        done = [h[1].get("agent") for h in c.history if h[0] == "delegate_task"]
        for agent in ["researcher", "coder", "writer"]:
            if agent not in done:
                return [call("delegate_task", agent=agent, task="Pesquise arquitetura de agentes autonomos em detalhe.")]
        return "Relatorio consolidado."

    # cenario de autonomia
    if not c.called("list_agents"):
        return [call("list_agents")]
    delegated = {h[1].get("agent"): h[2] for h in c.history if h[0] == "delegate_task"}
    if "researcher" not in delegated:
        return [call("delegate_task", agent="researcher",
                     task="Consulte a base de conhecimento interna sobre a politica de aprovacao de ferramentas e o budget "
                          "de tokens dos agentes. Depois obtenha as condicoes atuais do tempo em Sao Paulo "
                          "(latitude -23.55, longitude -46.63). Devolva os fatos com a fonte.")]
    if "coder" not in delegated:
        return [call("delegate_task", agent="coder",
                     task="Crie o script custo.py que calcula o custo de 1000 execucoes de agente com 25000 tokens de "
                          "entrada e 3000 de saida cada, a US$ 2,50 por milhao de entrada e US$ 10,00 por milhao de saida, "
                          "imprimindo o total. Execute com python3 e informe a saida exata.")]
    if "writer" not in delegated:
        return [call("delegate_task", agent="writer",
                     task="Escreva o arquivo relatorio_final.md com as secoes Governanca, Custo estimado e Clima, usando "
                          "exatamente estas informacoes.\n\n## Fatos do pesquisador\n" + delegated["researcher"] +
                          "\n\n## Resultado do script\n" + delegated["coder"])]
    custo = re.search(r"US\$ ?[\d.,]+", delegated["coder"])
    return ("Entrega concluida: relatorio_final.md gerado no workspace com Governanca (base interna), "
            f"Custo estimado ({custo.group(0) if custo else 'ver arquivo'}, calculado por custo.py) e Clima atual de Sao Paulo.")


def classify(t):
    """Classificador de triagem: decide so pelo pedido (antes da instrucao de formato, que cita as categorias)."""
    pedido = t.split("responda apenas")[0]
    if "sempre quebrado" in pedido or ("formato quebrado" in pedido and "resposta anterior" not in t):
        return "Acho que este pedido e do juridico."
    categoria = "juridico" if "contrato" in pedido else "financeiro" if "fatura" in pedido else "suporte"
    return json.dumps({"categoria": categoria, "urgente": "urgente" in pedido})


def researcher(c):
    t = c.task.lower()
    if "classifique" in t:
        return classify(t)

    if "pergunte a pessoa" in t:
        if not c.called("ask_human"):
            return [call("ask_human", question="Qual ambiente devo usar no deploy?", options=["homologacao", "producao"])]
        answer = c.result("ask_human")
        if answer.startswith("SEM RESPOSTA"):
            return "Sem resposta da pessoa no prazo; replanejei e sigo com homologacao, a opcao mais segura."
        return "A pessoa escolheu o ambiente: " + answer.replace("Resposta da pessoa:", "").strip()

    if "cep" in t:
        step = need_tool(c, "cep_lookup", "cep endereco")
        if step:
            return step
        if not c.called("cep_lookup"):
            cep = re.search(r"\d{8}", c.task.replace("-", ""))
            return [call("cep_lookup", cep=cep.group(0) if cep else "01310100")]
        r = c.result("cep_lookup")
        street = re.search(r'"street"\s*:\s*"([^"]+)"', r)
        hood = re.search(r'"neighborhood"\s*:\s*"([^"]+)"', r)
        return (f"Logradouro: {street.group(1) if street else '?'}; bairro: {hood.group(1) if hood else '?'} "
                "(fonte: BrasilAPI via cep_lookup).")

    if c.task.startswith("Tema:"):
        if not c.called("kb_search"):
            theme = c.task.split("\n")[0].replace("Tema:", "").strip()
            return [call("kb_search", query=theme, limit=3)]
        titles = re.findall(r"\[doc \d+\] ([^\n]+)", c.result("kb_search"))
        return "Fatos principais (fonte: base interna):\n" + "\n".join(f"- {x}" for x in titles)

    if "riscos" in t:
        return ("1. Loop de ferramentas sem fim -> mitigacao: max_steps por agente.\n"
                "2. Custo descontrolado -> mitigacao: budget de tokens hierarquico.\n"
                "3. Perda de progresso em queda -> mitigacao: checkpoint por no e retomada.")

    if not c.called("kb_search"):
        return [call("kb_search", query="politica aprovacao ferramentas budget tokens", limit=3)]
    if "latitude" in t:
        step = need_tool(c, "weather_forecast", "clima tempo previsao")
        if step:
            return step
        if not c.called("weather_forecast"):
            return [call("weather_forecast", latitude=-23.55, longitude=-46.63)]
    kb = c.result("kb_search")
    weather = c.result("weather_forecast")
    temp = re.search(r'"temperature_2m"\s*:\s*(-?[\d.]+)', weather.split('"current"')[-1]) if weather else None
    return ("Fatos da base interna:\n" + kb[:900] +
            (f"\n\nClima atual em Sao Paulo (Open-Meteo): {temp.group(1)} C." if temp else ""))


def coder(c):
    t = c.task.lower()
    if "ls -la" in t:
        step = need_tool(c, "run_shell", "executar comando shell")
        if step:
            return step
        if not c.called("run_shell"):
            return [call("run_shell", command="ls -la")]
        step = need_tool(c, "delete_file", "apagar arquivo")
        if step:
            return step
        if not c.called("delete_file"):
            path = re.search(r"apague o arquivo (\S+)", c.task)
            return [call("delete_file", path=path.group(1) if path else "x")]
        return (f"run_shell: {c.result('run_shell')[:160]}\n"
                f"delete_file: {c.result('delete_file')[:160]}\n"
                "Nenhuma das duas acoes foi executada por causa das politicas do harness.")

    if "custo.py" in t:
        name, code = "custo.py", (
            "execucoes = 1000\nentrada, saida = 25000, 3000\npreco_in, preco_out = 2.50, 10.00\n"
            "total = execucoes * (entrada * preco_in + saida * preco_out) / 1_000_000\n"
            "print(f'Custo total: US$ {total:.2f}')\n")
    else:
        name, code = "primos.py", (
            "primos = []\nn = 2\nwhile len(primos) < 10:\n"
            "    if all(n % p for p in primos):\n        primos.append(n)\n    n += 1\n"
            "print(' '.join(map(str, primos)))\n")
    if not c.called("write_file"):
        return [call("write_file", path=name, content=code)]
    step = need_tool(c, "run_shell", "executar script python")
    if step:
        return step
    if not c.called("run_shell"):
        return [call("run_shell", command=f"python3 {name}")]
    out = re.search(r"--- stdout ---\n(.*?)\n--- stderr", c.result("run_shell"), re.S)
    return f"Executei {name}. Saida real: {out.group(1).strip() if out else c.result('run_shell')[:200]}"


def writer(c):
    t = c.task.lower()
    if "haiku" in t:
        if not c.called("write_file"):
            return [call("write_file", path="haiku.md",
                         content="# Haiku\n\nfila silenciosa\nagentes pegam tarefas\no log guarda tudo\n")]
        return "haiku.md salvo."
    if "relatorio_final.md" in t:
        if not c.called("write_file"):
            facts = c.task.split("## Fatos do pesquisador")[-1].split("## Resultado do script")[0].strip()
            script = c.task.split("## Resultado do script")[-1].strip()
            clima = re.search(r"Clima atual[^\n]*", facts)
            content = ("# Relatorio final\n\n## Governanca\n" + re.sub(r"\[resultado de .*?\]\n?", "", facts.split("Clima atual")[0]).strip() +
                       "\n\n## Custo estimado\n" + re.sub(r"\[resultado de .*?\]\n?", "", script) +
                       "\n\n## Clima\n" + (clima.group(0) if clima else "indisponivel") + "\n")
            return [call("write_file", path="relatorio_final.md", content=content)]
        return "relatorio_final.md salvo."
    if "relatorio_workflow.md" in t:
        if not c.called("write_file"):
            return [call("write_file", path="relatorio_workflow.md", content="# Relatorio tecnico\n\n" + c.task.split("\n\n", 1)[-1])]
        return "relatorio_workflow.md salvo."
    lines = [l.strip("- ").strip() for l in c.task.split("\n")[1:] if l.strip()]
    return "\n".join(f"- {l}" for l in lines[:5]) or "- (sem conteudo)"


CUSTO_TASK = ("Crie o script custo.py que calcula o custo de 1000 execucoes de agente com 25000 tokens de entrada e 3000 de "
              "saida cada, a US$ 2,50 por milhao de entrada e US$ 10,00 por milhao de saida, imprimindo o total. "
              "Execute com python3 e informe a saida exata.")


def plan(*statuses):
    steps = ["Delegar o calculo ao programador", "Conferir a saida real do script", "Responder com o total"]
    return call("update_plan", steps=[{"step": s, "status": st} for s, st in zip(steps, statuses)])


def assistant(c):
    """Agente de chat: decide pelo texto da ULTIMA mensagem do usuario e pelo que ja fez neste turno."""
    text = c.last_user
    t = text.lower()

    if "[aviso do sistema" in t:
        notice = text.split("[Mensagem do usuario]")[0]
        return "Chegou o resultado da tarefa em segundo plano:\n\n" + notice.split("]", 1)[-1].strip()

    if "relatorio" in t:
        if not c.turn_called("ask_human"):
            return [call("ask_human", question="Prefere o relatorio em markdown ou em texto simples?", options=["markdown", "texto simples"])]
        answer = c.turn_result("ask_human").replace("Resposta da pessoa:", "").strip()
        return f"Combinado: o relatorio sai em **{answer}**."

    if "baixe" in t:
        url = re.search(r"https?://\S+", text)
        if not c.turn_called("http_get"):
            return [call("http_get", url=url.group(0) if url else "http://localhost")]
        return "Resultado do download: " + c.turn_result("http_get")[:160]

    if "apague tudo" in t:
        step = need_tool(c, "run_shell", "executar comando shell")
        if step:
            return step
        if not c.turn_called("run_shell"):
            return [call("run_shell", command="rm -rf ./*")]
        return "Nao foi possivel apagar: " + c.turn_result("run_shell")[:200]

    if "espere" in t:
        step = need_tool(c, "wait_seconds", "esperar aguardar segundos")
        if step:
            return step
        if not c.turn_called("wait_seconds"):
            seconds = re.search(r"(\d+) segundos", t)
            return [call("wait_seconds", seconds=int(seconds.group(1)) if seconds else 5)]
        return "Esperei e terminei: " + c.turn_result("wait_seconds")

    if "comando lento" in t:
        step = need_tool(c, "run_shell", "executar comando shell")
        if step:
            return step
        if not c.turn_called("run_shell"):
            return [call("run_shell", command="sleep 6 && echo executado >> efeito_lento.txt")]
        return "Resultado do comando: " + c.turn_result("run_shell")[:200]

    if "segundo plano" in t:
        if not c.turn_called("create_task"):
            return [call("create_task", agent="researcher", task="Consulte o endereco do CEP 01310100 e informe logradouro e bairro.")]
        return "Criei a tarefa em segundo plano para o researcher. Aviso quando terminar: " + c.turn_result("create_task")

    if "pesquise" in t:
        if not c.turn_called("web_search"):
            query = re.sub(r"(?i)pesquise( na web)?( sobre)?", "", text).strip(" ?.:") or "durable execution"
            return [call("think", thought="E um fato externo que pode ter mudado: preciso pesquisar antes de responder."),
                    call("web_search", query=query[:80])]
        r = c.turn_result("web_search")
        urls = re.findall(r"https?://\S+", r)
        titles = re.findall(r"^\d+\. (.+)$", r, re.M)
        return (f"Pelo que encontrei, o destaque e **{titles[0] if titles else 'sem titulo'}**.\n\n"
                "Fontes:\n" + "\n".join(f"- {u}" for u in urls[:3]))

    if "qual foi" in t or "lembra" in t:
        found = re.search(r"US\$ ?[\d.,]+", " ".join(c.assistant_texts) + " " + c.system)
        return (f"O total calculado foi **{found.group(0)}**, que veio da execucao do custo.py no turno anterior."
                if found else "Nao encontrei esse valor na nossa conversa.")

    if "calcule" in t or "script" in t:
        if not c.turn_called("update_plan"):
            return [plan("in_progress", "pending", "pending")]
        if not c.turn_called("delegate_task"):
            return [call("delegate_task", agent="coder", task=CUSTO_TASK)]
        if c.turn_called("update_plan") < 2:
            return [plan("done", "done", "in_progress")]
        total = re.search(r"US\$ ?[\d.,]+", c.turn_result("delegate_task"))
        return f"O script custo.py foi executado pelo programador e o total real e **{total.group(0) if total else '?'}**."

    if "shell" in t:
        step = need_tool(c, "run_shell", "executar comando shell")
        if step:
            return step
        if not c.turn_called("run_shell"):
            return [call("run_shell", command="ls")]
        return "Tentei usar o shell. Resultado: " + c.turn_result("run_shell")[:200]

    return "Ola! Posso pesquisar na web, calcular com scripts via especialistas, rodar tarefas em segundo plano e mais."


def summarizer(c):
    found = re.findall(r"US\$ ?[\d.,]+", c.task)
    return ("- O usuario pediu pesquisa na web e calculos de custo.\n"
            f"- Valores ja calculados: {', '.join(sorted(set(found))) or 'nenhum'}.\n"
            "- Arquivos: custo.py no workspace.")


def integration(c):
    if not c.called("ping"):
        return [call("ping", valor=42)]
    return "ok"


def decide(c):
    s = c.system.lower()
    if "teste de integracao" in s:
        return integration(c)
    if "voce resume conversas" in s:
        return summarizer(c)
    if "assistente principal" in s:
        return assistant(c)
    if "orquestrador" in s:
        return orchestrator(c)
    if "pesquisador" in s:
        return researcher(c)
    if "programador" in s:
        return coder(c)
    if "redator" in s:
        return writer(c)
    return "Sou um dublê de LLM e nao reconheci este agente."


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        """APIs publicas simuladas (Open-Meteo e BrasilAPI) para ambientes sem internet."""
        if self.path.startswith("/fake/weather"):
            payload = {"latitude": -23.55, "longitude": -46.63,
                       "current": {"time": time.strftime("%Y-%m-%dT%H:%M"), "temperature_2m": 24.3,
                                   "relative_humidity_2m": 61, "wind_speed_10m": 9.4}}
        elif self.path.startswith("/fake/cep/"):
            payload = {"cep": self.path.rsplit("/", 1)[-1], "state": "SP", "city": "Sao Paulo",
                       "neighborhood": "Bela Vista", "street": "Avenida Paulista", "service": "fake"}
        elif self.path.startswith("/fake/search"):
            payload = {"results": [
                {"title": "The definitive guide to Durable Execution | Temporal",
                 "url": "https://temporal.io/blog/what-is-durable-execution",
                 "content": "Durable Execution lets applications survive crashes and resume where they left off."},
                {"title": "Durable workflows in Postgres (DBOS)",
                 "url": "https://supabase.com/blog/durable-workflows-in-postgres-dbos",
                 "content": "DBOS checkpoints each workflow step in Postgres."}]}
        else:
            self.send_response(404); self.end_headers(); return
        data = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        time.sleep(LATENCY)
        if self.path.endswith("/v1/messages"):
            return self.anthropic(body)
        decision = decide(Conversation(body))
        if isinstance(decision, str):
            message = {"role": "assistant", "content": decision}
            finish = "stop"
        else:
            message = {"role": "assistant", "content": None, "tool_calls": [
                {"id": "call_" + uuid.uuid4().hex[:12], "type": "function",
                 "function": {"name": n, "arguments": json.dumps(a, ensure_ascii=False)}} for n, a in decision]}
            finish = "tool_calls"
        usage = {"prompt_tokens": len(json.dumps(body)) // 4, "completion_tokens": len(json.dumps(message)) // 4 + 5}
        if body.get("stream"):
            return self.stream_openai(message, finish, usage)
        resp = {
            "id": "mock-" + uuid.uuid4().hex[:8], "object": "chat.completion", "model": body.get("model"),
            "choices": [{"index": 0, "message": message, "finish_reason": finish}],
            "usage": usage,
        }
        data = json.dumps(resp).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def start_sse(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()

    def sse(self, payload, event=None):
        prefix = f"event: {event}\n" if event else ""
        self.wfile.write(f"{prefix}data: {json.dumps(payload, ensure_ascii=False)}\n\n".encode())
        self.wfile.flush()
        time.sleep(STREAM_DELAY)

    def stream_openai(self, message, finish, usage):
        """Mesmo formato do Azure/OpenAI com stream=true: deltas de texto e de tool_calls, depois uso e [DONE]."""
        self.start_sse()
        base = {"id": "mock-" + uuid.uuid4().hex[:8], "object": "chat.completion.chunk"}
        self.sse({**base, "choices": [], "prompt_filter_results": []})
        for piece in chunks(message.get("content") or ""):
            self.sse({**base, "choices": [{"index": 0, "delta": {"content": piece}, "finish_reason": None}]})
        for i, tc in enumerate(message.get("tool_calls") or []):
            self.sse({**base, "choices": [{"index": 0, "delta": {"tool_calls": [
                {"index": i, "id": tc["id"], "type": "function", "function": {"name": tc["function"]["name"], "arguments": ""}}]}}]})
            for piece in chunks(tc["function"]["arguments"]):
                self.sse({**base, "choices": [{"index": 0, "delta": {"tool_calls": [{"index": i, "function": {"arguments": piece}}]}}]})
        self.sse({**base, "choices": [{"index": 0, "delta": {}, "finish_reason": finish}]})
        self.sse({**base, "choices": [], "usage": usage})
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


STREAM_DELAY = float(os.environ.get("MOCK_LLM_STREAM_DELAY", "0.01"))


def chunks(text, size=12):
    return [text[i:i + size] for i in range(0, len(text), size)]


def anthropic_to_openai(body):
    """Converte uma requisicao da Messages API (Anthropic) no formato usado por Conversation."""
    msgs = [{"role": "system", "content": body.get("system", "")}]
    for m in body.get("messages", []):
        content = m["content"]
        if isinstance(content, str):
            msgs.append({"role": m["role"], "content": content})
            continue
        if m["role"] == "assistant":
            text = "".join(b.get("text", "") for b in content if b["type"] == "text")
            calls = [{"id": b["id"], "type": "function",
                      "function": {"name": b["name"], "arguments": json.dumps(b.get("input", {}))}}
                     for b in content if b["type"] == "tool_use"]
            msgs.append({"role": "assistant", "content": text or None, "tool_calls": calls})
        else:
            for b in content:
                if b["type"] == "tool_result":
                    msgs.append({"role": "tool", "tool_call_id": b["tool_use_id"], "content": b.get("content", "")})
                elif b["type"] == "text":
                    msgs.append({"role": "user", "content": b["text"]})
    return {"messages": msgs, "tools": [{"function": {"name": t["name"]}} for t in body.get("tools", [])]}


def _anthropic(self, body):
    decision = decide(Conversation(anthropic_to_openai(body)))
    if isinstance(decision, str):
        content, stop = [{"type": "text", "text": decision}], "end_turn"
    else:
        content = [{"type": "tool_use", "id": "toolu_" + uuid.uuid4().hex[:12], "name": n, "input": a} for n, a in decision]
        stop = "tool_use"
    usage = {"input_tokens": len(json.dumps(body)) // 4, "output_tokens": len(json.dumps(content)) // 4 + 5}
    if body.get("stream"):
        return _stream_anthropic(self, content, stop, usage)
    resp = {"id": "msg_" + uuid.uuid4().hex[:8], "type": "message", "role": "assistant", "model": body.get("model"),
            "content": content, "stop_reason": stop, "usage": usage}
    data = json.dumps(resp).encode()
    self.send_response(200)
    self.send_header("Content-Type", "application/json")
    self.send_header("Content-Length", str(len(data)))
    self.end_headers()
    self.wfile.write(data)


def _stream_anthropic(self, content, stop, usage):
    """Mesmo formato da Messages API com stream=true: blocos de texto e tool_use com input_json_delta."""
    self.start_sse()
    self.sse({"type": "message_start", "message": {"usage": {"input_tokens": usage["input_tokens"], "output_tokens": 1}}}, "message_start")
    for i, block in enumerate(content):
        if block["type"] == "text":
            self.sse({"type": "content_block_start", "index": i, "content_block": {"type": "text", "text": ""}}, "content_block_start")
            for piece in chunks(block["text"]):
                self.sse({"type": "content_block_delta", "index": i, "delta": {"type": "text_delta", "text": piece}}, "content_block_delta")
        else:
            self.sse({"type": "content_block_start", "index": i,
                      "content_block": {"type": "tool_use", "id": block["id"], "name": block["name"], "input": {}}}, "content_block_start")
            for piece in chunks(json.dumps(block["input"], ensure_ascii=False)):
                self.sse({"type": "content_block_delta", "index": i, "delta": {"type": "input_json_delta", "partial_json": piece}}, "content_block_delta")
        self.sse({"type": "content_block_stop", "index": i}, "content_block_stop")
    self.sse({"type": "message_delta", "delta": {"stop_reason": stop}, "usage": {"output_tokens": usage["output_tokens"]}}, "message_delta")
    self.sse({"type": "message_stop"}, "message_stop")


Handler.anthropic = _anthropic


if __name__ == "__main__":
    print(f"Dublê de LLM (OpenAI-compatível) em http://localhost:{PORT}/openai/v1")
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
