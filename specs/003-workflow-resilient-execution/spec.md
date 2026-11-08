# Feature Specification: Execucao resiliente de workflows

**Feature Branch**: `003-workflow-resilient-execution`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "Retry por no com backoff, timeout por no e do workflow, no opcional (continue_on_error), compensacao, cancelamento propagado, limite de paralelismo, retomada automatica pelo despachante com lease, versao da definicao fixa por run, retomar o agente do no do ponto onde parou, idempotencia dos nos de ferramenta com efeito."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Retomada automatica apos queda (Priority: P1)

O processo que executa um workflow morre. Hoje alguem precisa perceber e rodar a retomada a mao. O
operador espera que outra instancia retome sozinha, sem repetir nos concluidos.

**Why this priority**: e o mesmo nivel de durabilidade que o chat ja tem (Principio I).

**Independent Test**: queda abrupta do processo no meio de um workflow; outra instancia retoma, registra a recuperacao e
conclui, com cada no concluido executado uma unica vez.

**Acceptance Scenarios**:

1. **Given** um workflow em execucao cujo processo morreu, **When** o prazo de posse vence, **Then** outra
   instancia o retoma e registra quem era o dono anterior e quem retomou.
2. **Given** duas instancias disponiveis, **When** ha workflows orfaos, **Then** cada um e retomado por uma
   so.
3. **Given** um no de agente interrompido no meio, **When** retomado, **Then** o agente continua do
   historico gravado, sem refazer passos concluidos.
4. **Given** um no de ferramenta com efeito interrompido, **When** retomado, **Then** vira pedido humano
   (repetir, pular, informar), nunca repeticao automatica.

---

### User Story 2 - Versao fixa (Priority: P1)

Alguem altera a definicao de um workflow enquanto ha execucoes em andamento. Cada execucao MUST terminar
com a definicao com que comecou.

**Independent Test**: iniciar, alterar a definicao, derrubar e retomar; a execucao segue a versao antiga
e uma nova execucao usa a nova.

**Acceptance Scenarios**:

1. **Given** uma execucao iniciada na versao 1, **When** a versao 2 e publicada e a execucao e retomada,
   **Then** segue a versao 1.

---

### User Story 3 - Falhas transitorias e prazos (Priority: P2)

Um no que chama um servico instavel MUST tentar de novo com espera crescente; um no travado MUST parar
no prazo; um no opcional que falha nao derruba o fluxo.

**Independent Test**: no que falha duas vezes e passa na terceira; no que excede o prazo; no opcional que
falha e o workflow conclui.

**Acceptance Scenarios**:

1. **Given** um no com 3 tentativas, **When** as duas primeiras falham, **Then** a terceira executa e o
   numero de tentativas fica registrado.
2. **Given** um no com prazo de 30 s, **When** excede, **Then** e interrompido e tratado como falha
   (sujeito a novas tentativas).
3. **Given** um no opcional, **When** falha de vez, **Then** o fluxo segue e o no aparece como falho.
4. **Given** um workflow com prazo total, **When** vence, **Then** termina como expirado e os nos em
   execucao sao cancelados.
5. **Given** um no com efeito, **When** novas tentativas sao configuradas, **Then** so repete
   automaticamente se a falha ocorreu antes de o efeito comecar.

---

### User Story 4 - Cancelar e compensar (Priority: P3)

O operador cancela um workflow em andamento; os agentes em execucao param. Se um passo posterior falha,
os passos com compensacao declarada sao desfeitos em ordem inversa.

**Acceptance Scenarios**:

1. **Given** um workflow com nos em execucao, **When** cancelado, **Then** todos os runs relacionados
   terminam como cancelados e nada mais e iniciado.
2. **Given** nos A e B concluidos com compensacao e C falhando, **When** a compensacao roda, **Then**
   desfaz B e depois A, registrando cada passo.

### Edge Cases

- Retomada de execucao iniciada antes desta feature (sem versao fixa): usa a definicao atual e registra o
  aviso.
- Limite de paralelismo menor que o numero de ramos prontos: os demais esperam na ordem da definicao.
- Compensacao que falha: o workflow termina como "compensacao falhou" e pede atencao humana.
- Cancelamento durante espera de nova tentativa.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Workflows em execucao MUST ter dono com prazo renovado; vencido o prazo, qualquer instancia
  MUST poder retomar, uma unica vez.
- **FR-002**: A retomada MUST reaproveitar nos concluidos e continuar nos de agente do historico gravado.
- **FR-003**: Nos de ferramenta com efeito interrompidos MUST virar pedido humano.
- **FR-004**: Cada execucao MUST guardar a versao da definicao e usa-la ate o fim.
- **FR-005**: Cada no MUST aceitar novas tentativas (quantidade, espera inicial, fator, quais erros).
- **FR-006**: Cada no e o workflow MUST aceitar prazo.
- **FR-007**: Um no MUST poder ser marcado como opcional.
- **FR-008**: O operador MUST poder cancelar um workflow, com propagacao aos runs de agente.
- **FR-009**: O workflow MUST aceitar limite de nos em paralelo.
- **FR-010**: Nos MUST poder declarar uma compensacao, executada em ordem inversa quando o workflow falha.
- **FR-011**: Toda tentativa, prazo vencido, cancelamento, recuperacao e compensacao MUST ser registrado.

### Key Entities

- **Execucao de workflow**: versao fixada, dono, prazo de posse, status (inclui cancelado, expirado).
- **Execucao de no**: tentativas, prazo, status, compensacao.
- **Politica de tentativa**: quantidade, espera, fator, erros elegiveis.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% dos workflows orfaos sao retomados automaticamente em ate 2x o prazo de posse.
- **SC-002**: 0 nos concluidos reexecutados apos queda; 0 efeitos repetidos sem decisao humana.
- **SC-003**: 100% das execucoes terminam na versao com que comecaram.
- **SC-004**: Falhas transitorias que se resolvem dentro das tentativas nao derrubam o workflow.

## Assumptions

- Reusa o mecanismo de posse, heartbeat e despachante ja existente para runs.
- Prazo de posse padrao igual ao dos runs (60 s).
- Compensacao e opcional por no; sem ela, nada e desfeito.
