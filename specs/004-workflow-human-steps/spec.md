# Feature Specification: Passos humanos duraveis em workflows

**Feature Branch**: `004-workflow-human-steps`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "No human (aprovacao, pergunta, formulario) com espera duravel no meio do fluxo, reaproveitando os pedidos humanos e o despachante; espera humana duravel tambem para agentes e ferramentas dentro dos nos; caixa de tarefas humanas."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Aprovar no meio do fluxo (Priority: P1)

Um workflow de publicacao precisa que um responsavel aprove o texto antes de publicar. O fluxo para, o
responsavel recebe o pedido, aprova ou nega com motivo (dias depois, se preciso) e o fluxo continua pelo
ramo correspondente.

**Why this priority**: desbloqueia fluxos reais com governanca e substitui o modo de aprovacao do
terminal.

**Independent Test**: workflow com no humano; a execucao fica esperando sem processo, sobrevive a
reinicio e continua depois da decisao.

**Acceptance Scenarios**:

1. **Given** um workflow que chega a um no de aprovacao, **When** o no e alcancado, **Then** o workflow
   fica "esperando pessoa" e nenhum processo fica preso.
2. **Given** o pedido pendente e todos os processos reiniciados, **When** a pessoa aprova, **Then** o
   workflow continua pelo ramo "aprovado".
3. **Given** a pessoa nega com motivo, **When** o workflow continua, **Then** segue pelo ramo "negado" e o
   motivo fica disponivel aos nos seguintes.
4. **Given** o prazo vence, **When** a varredura passa, **Then** o pedido vira expirado e o workflow segue
   pelo ramo de expiracao ou falha, conforme a definicao.

---

### User Story 2 - Perguntas e formularios (Priority: P2)

Um no humano pede dados (ex.: centro de custo, data de inicio) num formulario com campos tipados; as
respostas viram campos usados pelos nos seguintes.

**Acceptance Scenarios**:

1. **Given** um formulario com campos obrigatorios, **When** a resposta omite um campo, **Then** e
   rejeitada e o pedido continua pendente.
2. **Given** a resposta valida, **When** o workflow continua, **Then** os nos seguintes acessam cada campo.

---

### User Story 3 - Aprovacao duravel dentro de nos de agente (Priority: P1)

O agente de um no decide usar uma ferramenta que exige aprovacao. Em vez de negar (padrao da feature 001),
o pedido vai para uma pessoa e o workflow espera, como no chat.

**Acceptance Scenarios**:

1. **Given** um workflow configurado para pedir aprovacao a pessoas, **When** o agente de um no chama uma
   ferramenta de aprovacao, **Then** o run do agente e o workflow ficam esperando e retomam apos a
   decisao.
2. **Given** um subagente desse agente pede uma pessoa, **When** decidido, **Then** a cadeia ate o
   workflow retoma de baixo para cima.

---

### User Story 4 - Caixa de tarefas humanas (Priority: P3)

Uma pessoa ve todos os pedidos pendentes direcionados a ela (de chats e de workflows), com contexto,
prazo e acao.

**Acceptance Scenarios**:

1. **Given** pedidos de varias origens, **When** a pessoa abre a caixa, **Then** ve os pendentes
   ordenados por prazo, com a origem.
2. **Given** um pedido decidido por outra pessoa, **When** a caixa atualiza, **Then** ele sai da lista.

### Edge Cases

- Duas pessoas decidindo o mesmo pedido: so a primeira decisao vale.
- Workflow cancelado com pedido pendente: o pedido fecha como expirado com motivo de cancelamento.
- No humano dentro de um ramo paralelo: os outros ramos continuam enquanto ele espera.
- Decisao usada uma unica vez (Principio II).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST ter um no humano dos tipos aprovacao, pergunta e formulario.
- **FR-002**: A espera MUST ser duravel, com prazo, e sobreviver a reinicio de todos os processos.
- **FR-003**: Aprovado, negado e expirado MUST poder seguir para ramos diferentes.
- **FR-004**: Respostas MUST ficar disponiveis como campos aos nos seguintes.
- **FR-005**: Formularios MUST validar campos obrigatorios e tipos.
- **FR-006**: O workflow MUST poder escolher entre negar por padrao (001) ou pedir a pessoas nas
  ferramentas de aprovacao chamadas dentro de nos.
- **FR-007**: A suspensao de um agente ou subagente dentro de um no MUST suspender o workflow, que retoma
  quando a cadeia termina.
- **FR-008**: O pedido MUST poder indicar o destinatario (pessoa ou grupo).
- **FR-009**: MUST existir uma caixa de pedidos pendentes por destinatario.
- **FR-010**: Toda decisao MUST registrar quem, quando e o motivo.

### Key Entities

- **Pedido humano**: tipo, destinatario, conteudo, prazo, status, decisao, origem (chat, workflow, no).
- **No humano**: tipo, campos, destinatario, prazo, ramos por resultado.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% dos workflows esperando pessoa continuam corretamente apos reinicio de todos os processos.
- **SC-002**: 0 processos ocupados por workflows esperando pessoa.
- **SC-003**: Uma decisao chega ao fluxo e ele continua em ate 5 segundos com o despachante ativo.
- **SC-004**: 100% das decisoes rastreaveis a uma pessoa.

## Assumptions

- Reusa os pedidos humanos, a expiracao e o despachante ja existentes no chat.
- O destinatario depende de identificar usuarios; ate existir autenticacao, o destinatario e um rotulo
  livre e qualquer usuario da tela pode decidir.
