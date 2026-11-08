# Research: Aprovacao segura em workflows

Levantamento feito no codigo em 2026-11-08 (commit `c45febb`).

## Estado atual

- `Program.cs:37` define o modo global: `Approval.Mode = --approval ?? APPROVAL_MODE ?? ("ask" em task/chat, senao "auto")`.
  O comando `workflow` cai em `auto`.
- `WorkflowEngine.RunNodeAsync` chama `AgentRunner.RunAsync(agent, prompt, workflowRunId)` sem `hooks`; o
  `RunContext` recebe `Human = ModeHumanGate.Instance`, que le o `Approval.Mode` estatico.
- Subagentes herdam `parent.Human` (`AgentRunner.cs:92`).
- No de ferramenta: `AgentRunner.RunToolAsync` monta um `RunContext` sem `Human` (padrao
  `ModeHumanGate.Instance`) e chama `ToolExecutor.ExecuteAsync`, com a mesma politica.
- A politica efetiva vem de `ToolPolicy.Resolve(policy, configJson, args)` (pura), chamada so pelo
  `ToolExecutor`. Ordem: `deny` global → `deny_when` → `approval` → `approval_when` → `auto`.
- O evento `policy` grava `decision`, `reason` e `policy_reason`, mas nao a origem de forma estruturada.
- Ferramentas nao `auto` no seed: `run_shell` (approval), `delete_file` (deny), `http_get` (auto com
  `approval_when`); MCP sincronizado entra como `approval`.
- O workflow do seed (`relatorio_tecnico`) nao usa ferramentas de aprovacao; os cenarios `workflow` e
  `retomada` nao dependem do modo.

## Decisoes

### D1. Modo de aprovacao por execucao de workflow, nao global

- **Decision**: o modo vira parametro de `WorkflowEngine.RunAsync` e gera um gate fixo para aquela
  execucao, passado por `RunHooks` aos nos de agente (e herdado pelos subagentes) e ao `RunContext` do no
  de ferramenta. Sem modo informado, `deny`. O modo e a origem (padrao ou explicito) sao gravados em
  `workflow_runs` e reaproveitados no `--resume`; um `--approval` explicito na retomada substitui e fica
  registrado.
- **Rationale**: o estatico global mistura modos de comandos diferentes no mesmo processo (a demo troca
  `Approval.Mode` entre cenarios) e nao garante o padrao seguro (Principio III). Gravar o modo cumpre o
  Principio I (retomada rele a decisao).
- **Alternatives considered**: (a) so trocar o padrao do comando `workflow` para `deny` no `Program.cs`:
  uma linha, mas a demo e o `serve` continuariam herdando o global e a retomada poderia rodar com outro
  modo; (b) novo `WorkflowHumanGate`: duplicaria a logica de terminal do `ModeHumanGate`.

### D2. `ModeHumanGate` com modo fixo

- **Decision**: `ModeHumanGate` ganha construtor com modo e origem; `ModeHumanGate.Instance` continua lendo
  `Approval.Mode` (chat, fila, demo inalterados). O workflow usa `ModeHumanGate.For(mode, explicit)`.
  No padrao, a negacao diz "negado por padrao em workflow (sem modo de aprovacao informado)".
- **Rationale**: uma classe so para os modos `auto`/`deny`/`ask`; FR-007 (chat e fila sem mudanca).

### D3. Regras de ferramenta na definicao do workflow

- **Decision**: bloco opcional `"tools": {"allow": [...], "block": [...]}` no topo da definicao, com os
  mesmos curingas de `allowed_tools` (`Glob.Match`). Viaja com a definicao versionada.
- **Precedencia** (da mais forte para a mais fraca):
  1. politica global `deny`;
  2. `deny_when` (bloqueio por argumento);
  3. `block` do workflow;
  4. `approval_when` continua exigindo aprovacao mesmo com `allow`;
  5. `allow` do workflow: transforma a politica base `approval` em `auto`;
  6. politica base da ferramenta.
- **Rationale**: FR-004 manda bloqueio vencer e regras de bloqueio por argumento sempre valerem.
  `approval_when` tambem e mantido porque escala casos perigosos (URL interna); liberar a ferramenta nao
  deve liberar o caso escalado. `allow` nunca supera um `deny` global.
- **Alternatives considered**: regras por no (mais fino, mas o spec pede por workflow; pode vir depois);
  `allow` superando `approval_when` (rejeitado: abre SSRF por configuracao).

### D4. Regras levadas pelo `RunContext`

- **Decision**: `RunContext.ToolRules` (opcional), vindo de `RunHooks.ToolRules` ou herdado do pai.
  `ToolExecutor` passa as regras a `ToolPolicy.Resolve`, que continua pura.
- **Rationale**: o mesmo caminho de heranca que o gate ja usa cumpre FR-005; a regra fica testavel em
  unidade.
- **Limite**: runs de workflow nao suspendem hoje (o gate de modo nunca suspende), entao a retomada pelo
  despachante nao precisa reconstruir as regras. Quando a 004 trouxer espera duravel em nos, as regras
  precisam ser persistidas no run; registrado como dependencia da 004.

### D5. Validacao das regras no inicio

- **Decision**: antes de criar o `workflow_run`, cada padrao de `allow`/`block` precisa casar com pelo
  menos uma ferramenta do registro; senao o workflow nao inicia e a mensagem lista os padroes sem
  correspondencia. Padrao repetido nas duas listas nao e erro (bloqueio vence).
- **Rationale**: edge case do spec; erro de digitacao numa regra de bloqueio seria uma falsa protecao.

### D6. Origem estruturada da decisao

- **Decision**: o evento `policy` ganha o campo `source`:
  `tool_policy` | `deny_when` | `approval_when` | `workflow_block` | `workflow_allow` |
  `human` | `mode_default` | `mode_explicit`.
  `PolicyResult` ganha `Source`; `ApprovalDecision` ganha `Source` (padrao `human`). Para ferramentas que
  executam sem pedido por causa do `allow`, grava-se um evento `policy` com `decision = "approved"` e
  `source = "workflow_allow"` (hoje `auto` nao gera evento; o spec pede origem de 100% das decisoes em
  workflow, entao o evento so e gravado quando ha regras de workflow ou quando a decisao veio de
  aprovacao).
- **Rationale**: SC-002 e Principio III (decisao auditavel).

### D7. Prova

- Unidade: precedencia de `ToolPolicy.Resolve` com regras; `ModeHumanGate.For` (padrao nega, explicito
  aprova, origem correta); validacao dos padroes.
- Demo: cenario novo `workflow_aprovacao` com workflows temporarios (`demo_*`) cobrindo padrao deny, modo
  explicito, `allow`, `block`, `deny_when` com `allow`, e heranca pelo subagente (ramo novo no dublê: o
  orquestrador delega ao coder um `ls -la`).
- Todos os cenarios anteriores e `tests/*.sh` continuam passando.

## Sem pendencias

Nenhum NEEDS CLARIFICATION: o contexto tecnico e o do projeto, sem dependencias novas.
