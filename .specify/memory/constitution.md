<!--
Sync Impact Report
- Version change: (template) → 1.0.0
- Principles added: I. Estado duravel no banco; II. Efeito no maximo uma vez; III. Humano no controle,
  padrao seguro; IV. Teste antes, evidencia depois; V. Simplicidade e dependencias minimas
- Sections added: Restricoes tecnicas; Fluxo de desenvolvimento; Governance
- Sections removed: none
- Templates: plan-template.md "Constitution Check" le este arquivo em tempo de execucao; nenhum template alterado
- Deferred TODOs: none
-->

# AgentPoc Constitution

## Core Principles

### I. Estado duravel no banco

Todo trabalho que executa ou espera (run de agente, cadeia de subagentes, workflow, pedido a uma pessoa)
MUST ter seu estado gravado no Postgres antes de o processo seguir adiante.

- Nenhuma espera (humana, de subagente, de tempo ou de evento externo) MUST segurar memoria ou processo.
- Um `kill -9` em qualquer ponto MUST deixar o sistema num estado do qual outra instancia retoma sozinha:
  lease por execucao, heartbeat e recuperacao de orfaos.
- Transicoes que mexem em mais de uma linha (ex.: filho termina e pai recebe o resultado) MUST ser atomicas
  ou reconciliaveis por uma varredura.
- Retomar MUST reler o que ja foi decidido (saida de no, desvio escolhido, resposta do modelo) e nunca
  recalcular uma decisao nao deterministica.

Motivo: o produto promete execucao duravel; qualquer estado so em memoria quebra essa promessa.

### II. Efeito no maximo uma vez

Nada com efeito colateral MUST rodar duas vezes sem uma decisao explicita.

- Toda ferramenta declara se tem efeito (`side_effect`); o padrao e ter efeito.
- Uma chamada com efeito interrompida no meio MUST virar um pedido humano (repetir, pular ou informar o
  resultado), nunca uma repeticao automatica.
- Uma decisao humana vale para uma unica execucao e para a mesma ferramenta com os mesmos argumentos.
- Delegacoes e nos reaproveitam o filho ou o resultado ja criado na retomada.

Motivo: agentes operam sistemas reais; repetir um deploy ou um pagamento e pior do que parar e perguntar.

### III. Humano no controle, padrao seguro

Ferramentas e passos sensiveis MUST passar pelas politicas (`auto`, `approval`, `deny`, `deny_when`,
`approval_when`), em qualquer modo de execucao: chat, fila ou workflow.

- Quando nao ha quem responda, o padrao MUST ser negar, nao aprovar.
- Pedidos a pessoas MUST ser duraveis, ter prazo e terminar como `expired` quando vencem.
- Argumentos editados por uma pessoa MUST ser reavaliados pelas regras de bloqueio.
- Toda decisao MUST ficar registrada com quem decidiu, quando e o motivo.

Motivo: autonomia so e aceitavel com governanca verificavel.

### IV. Teste antes, evidencia depois (NAO NEGOCIAVEL)

Toda mudanca MUST comecar por um teste que falha (TDD) e terminar com evidencia executavel.

- Regras puras MUST ter testes de unidade (padrao AAA, nomes descritivos, sem dependencia entre testes).
- Comportamento de ponta a ponta MUST ter um cenario em `demo` com checagens objetivas.
- Garantias de durabilidade MUST ser provadas com queda real (`kill -9`) num script em `tests/`.
- Todos os testes e cenarios anteriores MUST continuar passando; uma falha preexistente MUST ser provada
  como preexistente e registrada.
- Testes MUST testar comportamento, nao implementacao.

Motivo: numa POC de durabilidade, a afirmacao so vale com a prova da queda.

### V. Simplicidade e dependencias minimas

- O nucleo MUST depender apenas de .NET 8 e Npgsql; uma dependencia nova MUST ser justificada no plano.
- Sem container de DI, sem feature flags e sem shims de compatibilidade sem necessidade real.
- `sql/schema.sql` MUST ser idempotente e `setup` sem `--reset` MUST aplicar sobre um banco existente
  sem perder dados.
- Abstracoes so quando houver mais de um uso; validacao so nas fronteiras (entrada de usuario, APIs
  externas, saida de modelo).
- Codigo auto-documentado: nomes que revelam intencao, funcoes pequenas, constantes nomeadas no lugar de
  valores fixos; comentario so para o porque de logica complexa, workaround ou API publica.

Motivo: a POC precisa continuar legivel e facil de levar para on-prem.

## Restricoes tecnicas

- Execucao: .NET 8, um binario com comandos (`setup`, `chat`, `serve`, `dispatcher`, `workflow`, `demo`).
- Estado: Postgres 16; filas e despacho com `FOR UPDATE SKIP LOCKED` e `LISTEN/NOTIFY`, sem motor externo.
- Modelos: provedores compativeis com OpenAI e Anthropic, com streaming; o dublê `tests/mock_llm.py`
  MUST cobrir todo cenario novo, para os testes rodarem sem chave e sem internet.
- Ferramentas: `builtin`, `http` e `mcp`, cadastradas no banco; comportamento muda por dado, nao por codigo.
- Infra local: `docker-compose.yml` com Postgres e, opcionalmente, SearXNG.
- Idioma: textos de produto, logs e documentacao em portugues; identificadores de codigo em ingles.

## Fluxo de desenvolvimento

- Commits pequenos e focados, em Conventional Commits, com unico autor André Abreu
  <andre.abreu@live.com>, sem co-autores.
- Nada de `push` sem pedido explicito.
- Cada feature segue o Spec Kit: `spec.md` → `plan.md` → `tasks.md` → implementacao.
- Ao terminar uma feature: README atualizado (Como funciona e Limitacoes), saida dos testes registrada,
  desvios do desenho e limites que ficaram descritos.
- Mudancas arquivo por arquivo, sem alterar codigo nao relacionado e sem inventar escopo.

## Governance

Esta constituicao prevalece sobre preferencias pontuais de implementacao. Todo `plan.md` MUST passar pelo
"Constitution Check" antes do desenho e de novo depois; uma violacao MUST ser justificada na tabela de
complexidade do plano ou o desenho muda.

Emendas: proposta escrita, motivo e impacto nos specs em andamento; aprovacao do dono do projeto.
Versionamento semantico: MAJOR para remover ou redefinir um principio, MINOR para principio ou secao nova,
PATCH para redacao.

**Version**: 1.0.0 | **Ratified**: 2026-11-08 | **Last Amended**: 2026-11-08
