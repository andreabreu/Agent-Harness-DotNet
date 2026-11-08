#!/usr/bin/env python3
"""Servidor MCP minimo via stdio (sem dependencias), para testar a integracao MCP da POC sem Node.

Ferramentas: echo(texto) e soma(a, b).
Cadastro:  insert into mcp_servers (id, command, args, enabled)
           values ('demo', 'python3', '{tests/mcp_echo_server.py}', true);
Depois:    dotnet run -- sync-mcp
"""
import json
import sys

TOOLS = [
    {"name": "echo", "description": "Devolve o texto recebido (eco), para testar MCP.",
     "inputSchema": {"type": "object", "properties": {"texto": {"type": "string"}}, "required": ["texto"]}},
    {"name": "soma", "description": "Soma dois numeros.",
     "inputSchema": {"type": "object", "properties": {"a": {"type": "number"}, "b": {"type": "number"}}, "required": ["a", "b"]}},
]


def reply(msg_id, result=None, error=None):
    out = {"jsonrpc": "2.0", "id": msg_id}
    out["error" if error else "result"] = error or result
    sys.stdout.write(json.dumps(out) + "\n")
    sys.stdout.flush()


for line in sys.stdin:
    if not line.strip():
        continue
    msg = json.loads(line)
    method, msg_id, params = msg.get("method"), msg.get("id"), msg.get("params") or {}
    if msg_id is None:
        continue  # notificacao
    if method == "initialize":
        reply(msg_id, {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}},
                       "serverInfo": {"name": "demo-echo", "version": "1.0"}})
    elif method == "tools/list":
        reply(msg_id, {"tools": TOOLS})
    elif method == "tools/call":
        name, args = params.get("name"), params.get("arguments") or {}
        if name == "echo":
            text = f"eco MCP: {args.get('texto', '')}"
        elif name == "soma":
            text = str(args.get("a", 0) + args.get("b", 0))
        else:
            reply(msg_id, {"content": [{"type": "text", "text": f"ferramenta desconhecida: {name}"}], "isError": True})
            continue
        reply(msg_id, {"content": [{"type": "text", "text": text}]})
    else:
        reply(msg_id, error={"code": -32601, "message": f"metodo nao suportado: {method}"})
