# Contrato: bloco `tools` na definicao de workflow

```json
{
  "tools": {
    "allow": ["mcp_filesystem_read_*", "run_shell"],
    "block": ["http_get"]
  },
  "nodes": { "...": "inalterado" }
}
```

- `tools`, `allow` e `block` sao opcionais; sem eles o comportamento e so o modo de aprovacao.
- Padroes: `*`, prefixo terminado em `*`, ou nome exato.
- Precedencia e origem de cada decisao: [data-model.md](../data-model.md#politica-efetiva-toolpolicyresolve--entrada-e-saida).
- Valem para nos `agent` (e seus subagentes) e nos `tool`.
- `allow` nunca libera ferramenta com politica global `deny`, nem argumentos que casam `deny_when` ou
  `approval_when`.
