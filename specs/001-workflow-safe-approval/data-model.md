# Data Model: Aprovacao segura em workflows

## Execucao de workflow (`workflow_runs`) — colunas novas

| Campo | Tipo | Regra |
|---|---|---|
| `approval_mode` | text, `deny` \| `auto` \| `ask` | Modo efetivo da execucao. Sem modo informado: `deny`. |
| `approval_mode_explicit` | boolean, padrao `false` | `true` quando veio de `--approval` ou `APPROVAL_MODE`. |

- Gravados ao criar a execucao; reaproveitados no `--resume`.
- `--approval` explicito na retomada substitui os dois valores e gera um log de aviso.
- Execucoes antigas (colunas nulas) retomam como `deny` padrao.
- Schema idempotente: `alter table workflow_runs add column if not exists ...`.

## Regras de ferramenta do workflow (`workflows.definition.tools`)

```json
{ "tools": { "allow": ["mcp_filesystem_read_*"], "block": ["run_shell"] }, "nodes": { ... } }
```

| Campo | Tipo | Regra |
|---|---|---|
| `allow` | lista de padroes | `*`, prefixo com `*` ou nome exato (`Glob.Match`). Opcional. |
| `block` | lista de padroes | Idem. Opcional. |

- Cada padrao MUST casar com pelo menos uma ferramenta registrada (validado no inicio da execucao).
- Faz parte da definicao, logo da versao (`workflows.version` sobe se mudar).

## Politica efetiva (`ToolPolicy.Resolve`) — entrada e saida

Entrada: politica da ferramenta, `config` (com `deny_when`/`approval_when`), argumentos, regras do workflow
(opcional).

Saida `PolicyResult(Decision, Reason, Source)`:

| Ordem | Condicao | Decision | Source |
|---|---|---|---|
| 1 | politica global `deny` | Deny | `tool_policy` |
| 2 | `deny_when` casa | Deny | `deny_when` |
| 3 | ferramenta em `block` | Deny | `workflow_block` |
| 4 | `approval_when` casa | Approval | `approval_when` |
| 5 | ferramenta em `allow` | Auto | `workflow_allow` |
| 6 | politica `approval` | Approval | `tool_policy` |
| 7 | caso contrario | Auto | `tool_policy` |

## Decisao de aprovacao (`ApprovalDecision`)

Campo novo `Source`:

| Gate | Situacao | Source |
|---|---|---|
| `ModeHumanGate.For(mode, explicit: false)` | padrao do workflow | `mode_default` |
| `ModeHumanGate.For(mode, explicit: true)` | modo informado | `mode_explicit` |
| `ModeHumanGate.Instance` | chat/fila/demo | `mode_explicit` (comportamento atual) |
| `DurableHumanGate` | pessoa decidiu | `human` |

## Evento `policy` (`run_events.data`)

Campos atuais mantidos (`tool`, `decision`, `reason`, `policy_reason`, `edited`...) e novos:

| Campo | Quando |
|---|---|
| `source` | sempre: origem final da decisao (tabela acima) |
| `policy_source` | quando houve aprovacao: a origem da exigencia (`tool_policy` ou `approval_when`) |

Gravacao do evento:
- `blocked` (Deny) e `approved`/`denied` (Approval) como hoje, com `source`;
- novo: `approved` com `source = workflow_allow` quando o `allow` dispensou a aprovacao.
