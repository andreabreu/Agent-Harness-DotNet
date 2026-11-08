# Contrato: comandos e definicao

## Comandos

```text
dotnet run -- workflow <id> <entrada...> [--approval ...]   # executa; mantem o lease enquanto roda
dotnet run -- workflow --resume <workflow_run_id>            # retomada manual (continua valida)
dotnet run -- workflow --cancel <workflow_run_id>            # pede cancelamento (novo)
dotnet run -- dispatcher | serve | chat                      # o despachante passa a retomar workflows orfaos
```

- `--cancel` de execucao ja terminada: mensagem e codigo 1, nada muda.
- `--resume` de execucao com dono vivo (lease valido): recusado, citando o dono.

## Definicao

Campos e regras: [data-model.md](../data-model.md#definicao-campos-novos). Exemplo:

```json
{
  "max_parallel": 2,
  "timeout_seconds": 600,
  "nodes": {
    "buscar":  {"type": "tool", "tool": "http_get", "args": {"url": "https://api/x"},
                "retry": {"max_attempts": 3, "backoff_seconds": 2, "factor": 2, "on": ["error", "timeout"]},
                "timeout_seconds": 30, "next": ["reservar"]},
    "reservar": {"type": "tool", "tool": "run_shell", "args": {"command": "reservar.sh"},
                 "compensate": {"type": "tool", "tool": "run_shell", "args": {"command": "cancelar_reserva.sh"}},
                 "next": ["notificar"]},
    "notificar": {"type": "agent", "agent": "writer", "input": "...", "optional": true}
  }
}
```

## Eventos (`workflow_events.type`)

`workflow_recovered`, `definition_not_pinned`, `node_retry`, `node_timeout`, `node_failed_optional`,
`workflow_cancelled`, `workflow_expired`, `compensation_started`, `node_compensated`, `compensation_failed`.
`data` traz o que explica o evento (ex.: `previous_owner`/`recovered_by`, `attempt`/`reason`/`wait_seconds`).
