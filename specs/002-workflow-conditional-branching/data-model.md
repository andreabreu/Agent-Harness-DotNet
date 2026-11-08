# Data Model: Desvio condicional e dados estruturados

## Banco

### `workflow_node_runs` (alteracoes)

| Campo | Mudanca | Regra |
|---|---|---|
| `status` | check passa a `running \| completed \| failed \| skipped` | `skipped`: ramo nao escolhido ou sem caminho ativo; nao executa |
| `chosen_next` | nova, `text[]` nula | so em no `condition` concluido: destinos escolhidos |

Idempotente: `drop constraint if exists workflow_node_runs_status_check` + `add constraint`;
`add column if not exists chosen_next text[]`.

### `workflow_runs`

Sem colunas novas. `output` passa a ser a saida configurada (`output` da definicao) ou a do `end`.

## Definicao do workflow (`workflows.definition`)

```json
{
  "input_schema": { "type": "object", "properties": { "texto": {"type": "string"} }, "required": ["texto"] },
  "tools": { "allow": [], "block": [] },
  "output": "{{consolidar}}",
  "nodes": {
    "classificar": { "type": "agent", "agent": "researcher", "input": "Classifique: {{input.texto}}",
                     "output_schema": { "type": "object",
                                        "properties": { "categoria": {"type": "string", "enum": ["juridico", "financeiro", "suporte"]},
                                                        "urgente": {"type": "boolean"} },
                                        "required": ["categoria", "urgente"] },
                     "output_retries": 2,
                     "next": ["rotear"] },
    "rotear": { "type": "condition",
                "cases": [ { "when": {"path": "classificar.urgente", "eq": true}, "next": ["escalar"] },
                           { "when": {"path": "classificar.categoria", "eq": "juridico"}, "next": ["juridico"] } ],
                "default": ["suporte"] },
    "consolidar": { "type": "transform", "join": "all_active",
                    "output": { "categoria": "{{classificar.categoria}}", "tratado_por": "{{suporte}}" }, "next": [] },
    "recusar": { "type": "end", "status": "failed", "output": "Pedido recusado: {{classificar.categoria}}" }
  }
}
```

### Campos comuns de no

| Campo | Tipo | Regra |
|---|---|---|
| `type` | `agent \| tool \| condition \| transform \| end` | obrigatorio |
| `next` | lista de ids | proibido em `condition` (usa `cases`/`default`) e em `end`; demais opcional |
| `join` | `all \| all_active \| any` | opcional, padrao `all` |

### Por tipo

| Tipo | Campos | Regra |
|---|---|---|
| `agent` | `agent`, `input`, `output_schema?`, `output_retries?` (0–5, padrao 2) | com schema, a saida gravada e o JSON validado |
| `tool` | `tool`, `args?` | `args` renderizado com tipo preservado |
| `condition` | `cases` (lista de `{when, next}`), `default?` | primeiro caso verdadeiro vence; sem caso e sem `default`, nenhum destino (todos pulados) |
| `transform` | `output` (qualquer JSON) | renderizado; saida gravada como JSON |
| `end` | `status` (`completed \| failed`), `output?` (template) | termina o workflow |

### Condicao (`when`)

| Forma | Exemplo |
|---|---|
| comparacao | `{"path": "classificar.score", "gte": 7}` — operadores `eq ne gt gte lt lte contains in exists` (um por objeto) |
| todas | `{"all": [cond, cond]}` |
| alguma | `{"any": [cond, cond]}` |
| negacao | `{"not": cond}` |

- `path`: `input.campo...` ou `<no>.campo...`, com `[n]` para listas.
- Tipos estritos: `gt/gte/lt/lte` so numero com numero; `contains` em texto ou lista; `in` com lista literal;
  `exists: true|false`. Caminho ausente: comparacoes dao falso, `exists` da falso.

### Schema (subconjunto de JSON Schema)

Palavras aceitas: `type`, `properties`, `required`, `enum`, `items`. Qualquer outra: erro de definicao.

## Estado do escalonador (memoria, por execucao)

| Estado de no | Origem |
|---|---|
| `pending` | ainda nao decidido |
| `running` | no lote atual |
| `completed` (+ `chosen_next` se desvio) | checkpoint |
| `failed` | checkpoint; o workflow termina como falho |
| `skipped` | decidido pelo escalonador; gravado |

Aresta `A → B`: *ativa* (A concluido e, se A e desvio, B ∈ `chosen_next`), *inativa* (A pulado, ou A desvio e
B ∉ `chosen_next`), *pendente* (caso contrario). Regras de `join`: [research.md](research.md#d4-escalonador-puro-separado-do-motor).
