# Contrato: comando `workflow`

```text
dotnet run -- workflow <id> <entrada...> [--approval deny|auto|ask] [--crash-after <no>]
dotnet run -- workflow --resume <workflow_run_id> [--approval deny|auto|ask]
```

| Origem do modo | Modo efetivo | `approval_mode_explicit` |
|---|---|---|
| `--approval X` | X | true |
| `APPROVAL_MODE=X` (ambiente ou `.env`) | X | true |
| nenhum | `deny` | false |
| `--resume` sem `--approval` | o gravado na execucao | o gravado |

- Valor invalido em `--approval`/`APPROVAL_MODE`: erro antes de iniciar, listando os valores aceitos.
- No inicio, o log mostra o modo e a origem, ex.: `aprovacao: deny (padrao de workflow)`.
- Regras `tools.allow`/`tools.block` com padrao sem ferramenta correspondente: o comando termina com erro e
  nao cria `workflow_run`.
- Os comandos `chat`, `serve`, `task`, `worker` e `demo` mantem o comportamento atual de `APPROVAL_MODE`.
