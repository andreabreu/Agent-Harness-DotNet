# Research: Desvio condicional e dados estruturados em workflows

Levantamento no codigo em 2026-11-08 (commit `fc8b381`).

## Estado atual

- `WorkflowEngine.RunAsync` (~250 linhas) faz tudo: carrega a definicao, monta antecessores a partir de `next`,
  roda em lotes os nos cujos antecessores estao todos `done`, grava checkpoint e calcula a saida final
  (concatenacao dos nos sem `next`).
- Tipos de no: `agent` e `tool`; qualquer outro falha na hora de executar ("tipo de no desconhecido").
- Saidas sao texto (`workflow_node_runs.output`); `{{x}}` aceita so `[A-Za-z0-9_]+` e referencia inexistente vira
  string vazia, sem erro.
- `workflow_node_runs.status` aceita `running | completed | failed`.
- Retomada (`--resume`) reaproveita nos `completed` e reexecuta o resto; usa a definicao atual (fixar a versao e da 003).
- Agentes devolvem texto livre; nao ha saida estruturada nem validacao.
- O dublê decide por palavras-chave na tarefa; nao tem ramo de classificacao.

## Decisoes

### D1. Condicoes como arvore JSON, nao como linguagem textual

- **Decision**: `when` e um objeto: comparacao `{"path": "classificar.categoria", "eq": "juridico"}` (operadores
  `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `contains`, `in`, `exists`) ou combinacao `{"all": [...]}`,
  `{"any": [...]}`, `{"not": {...}}`.
- **Rationale**: nao precisa de parser nem tokenizer; impossivel executar codigo arbitrario; validavel ao
  iniciar; facil de gerar no editor visual (008). Comparacao de tipos estrita (numero com numero, texto com texto),
  sem coercao implicita.
- **Alternatives considered**: expressoes em texto (`classificar.urgente == true`) — legiveis, mas exigem parser,
  mensagens de erro de sintaxe e cuidado com injecao; JSONLogic/CEL — dependencia nova (Principio V).

### D2. Saida estruturada por prompt + validacao + nova tentativa

- **Decision**: no `agent` com `output_schema` recebe, no fim do prompt, a instrucao de responder so com um JSON
  desse formato. A resposta e extraida (aceita bloco ```json), validada; se invalida, um novo run do agente recebe
  o prompt original mais os erros da validacao. Ate 2 novas tentativas (`output_retries`, padrao 2); depois o no
  falha com os erros.
- **Rationale**: funciona com qualquer provedor (OpenAI-compatible e Anthropic), inclusive o dublê; a validacao
  local e a garantia, nao o provedor (Principio IV: saida de modelo e fronteira).
- **Alternatives considered**: `response_format: json_schema` do provedor — nao uniforme entre provedores e
  modelos locais; pode ser uma otimizacao futura por modelo, mantendo a validacao local.

### D3. Formato de schema: subconjunto de JSON Schema

- **Decision**: `output_schema` e `input_schema` usam JSON Schema restrito a `type` (`object`, `string`,
  `number`, `integer`, `boolean`, `array`), `properties`, `required`, `enum`, `items`. Palavra-chave fora disso e
  rejeitada ao iniciar o workflow.
- **Rationale**: modelos entendem JSON Schema no prompt; o editor da 008 pode reaproveitar; validador proprio de
  ~80 linhas, sem dependencia.
- **Alternatives considered**: formato proprio (`{"campo": {"type": ...}}`) — mais simples, mas menos conhecido;
  biblioteca de JSON Schema — dependencia nova.

### D4. Escalonador puro, separado do motor

- **Decision**: `WorkflowScheduler` (puro) recebe o grafo e o estado de cada no (`pending`, `running`,
  `completed`, `failed`, `skipped`, mais os destinos escolhidos por cada desvio) e devolve quem esta pronto e quem
  deve ser pulado. O motor so executa, grava e repete.
- **Semantica das arestas**: uma aresta `A → B` fica *ativa* quando A concluiu e (A nao e desvio ou B esta entre
  os destinos escolhidos); fica *inativa* quando A foi pulado, ou A e desvio e B nao foi escolhido.
- **Joins** (`join` no no, padrao `all`):
  - `all`: pronto quando todas as arestas de entrada estao ativas; pulado se alguma ficar inativa.
  - `all_active` ("todos os nao pulados"): pronto quando nenhuma esta pendente e ao menos uma esta ativa; pulado
    se todas ficarem inativas.
  - `any`: pronto quando a primeira fica ativa (executa uma vez); pulado se todas ficarem inativas.
- **Rationale**: a logica de desvio e join e onde estao os casos de borda; isolada, e testavel em unidade sem
  banco (Principio IV) e reaproveitavel pela 003 e pela 006.

### D5. Escolha do desvio gravada

- **Decision**: no `condition` grava, no proprio `workflow_node_runs`, `status = completed`, `output` com o JSON
  `{"case": <indice ou "default">, "next": [...]}` e a coluna nova `chosen_next text[]`. Nos pulados ganham linha
  com `status = skipped`. A retomada reaproveita `completed` (com `chosen_next`) e `skipped` sem reavaliar.
- **Rationale**: FR-004 e Principio I; a escolha fica auditavel por SQL.

### D6. Referencias a campos e preservacao de tipo

- **Decision**: caminhos `no.campo.sub[0]` e `input.campo`. Valor de no e lido como JSON quando a saida e JSON
  valido; senao, so o nome do no (texto inteiro) e aceito. Caminho que nao existe faz o no que referencia falhar,
  citando o caminho. Em `transform` e `args` de no `tool`, uma string que e exatamente uma referencia
  (`"{{classificar.itens}}"`) preserva o tipo; referencias dentro de texto viram texto.
- **Compatibilidade**: `{{no}}` e `{{input}}` continuam como hoje (texto inteiro). Referencia a no inexistente
  passa a ser erro de definicao em todos os workflows, porque o vazio silencioso esconde erro de digitacao. Os
  workflows do seed nao dependem disso (verificado: `relatorio_tecnico` so referencia nos que existem).
- **Referencia a no pulado**: `{{no}}` de um no `skipped` vira texto vazio e caminho dentro dele conta como ausente
  (comum depois de um join `all_active`); isso nao e erro.

### D7. Nos `transform` e `end`

- **Decision**: `transform` tem `output` (qualquer JSON com referencias), sem modelo. `end` tem
  `status` (`completed` | `failed`) e `output` (template); ao ser executado, o motor para de agendar, espera o lote
  em andamento, marca os pendentes como `skipped` e termina com o status e a saida do `end`. Dois `end` no mesmo
  lote: `failed` vence; empate, o primeiro na ordem da definicao.
- **Rationale**: FR-009; termino explicito por ramo sem codigo.

### D8. Entrada tipada e saida final

- **Decision**: `input_schema` no topo da definicao; com ele, a entrada MUST ser um objeto JSON valido para o
  schema, senao o workflow nao inicia (lista os erros). `output` no topo (template ou JSON com referencias) define
  a saida final; sem ele, mantem a concatenacao dos terminais executados (pulados ficam de fora).

### D9. Validacao da definicao ao iniciar

- **Decision**: `WorkflowDefinition.Parse` valida e devolve todos os erros de uma vez, antes de criar a execucao:
  tipos de no conhecidos, campos obrigatorios por tipo, destinos existentes (em `next`, `cases[].next`,
  `default`), `join` valido, condicoes bem formadas, schemas no subconjunto, referencias `{{no...}}` a nos
  existentes. Ciclos continuam detectados como hoje.
- **Rationale**: edge case "condicao invalida rejeitada ao iniciar"; base para a validacao completa da 009.

### D10. Prova

- Unidade: `ConditionEvaluator`, `JsonSchemaLite`, `WorkflowValues` (caminhos, tipo preservado, erros),
  `WorkflowScheduler` (desvio, propagacao de pulados, os 3 joins, todos pulados), `WorkflowDefinition` (erros).
- Demo: cenario `workflow_desvio` com o workflow de triagem (classificador `researcher` com `output_schema`,
  desvio, 3 ramos, join `all_active`, `transform`, `end` com falha), cobrindo as historias; queda simulada apos o
  desvio e retomada sem chamar o classificador de novo.
- Dublê: ramo "classifique" no `researcher` devolvendo JSON por palavras-chave, com modo "formato quebrado" que
  erra na primeira tentativa e acerta ao receber os erros.
- Regressao: todos os cenarios e `tests/*.sh`.

## Sem pendencias

Nenhum NEEDS CLARIFICATION; nenhuma dependencia nova.
