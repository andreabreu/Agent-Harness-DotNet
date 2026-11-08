# Feature Specification: Disparo de workflows por API, agenda e eventos

**Feature Branch**: `007-workflow-triggers-api`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "API para iniciar workflow; gatilhos por agendamento (cron), webhook, tarefa da fila e a partir do chat (workflow como ferramenta); fila de execucoes com prioridade e concorrencia por workflow."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Iniciar e acompanhar por API (Priority: P1)

Um sistema externo inicia um workflow com uma entrada e consulta o status e a saida depois. Hoje so da
para iniciar pelo terminal.

**Why this priority**: e o requisito minimo para integrar o motor a outros sistemas e a uma tela.

**Independent Test**: iniciar por API, consultar ate concluir e ler a saida.

**Acceptance Scenarios**:

1. **Given** um workflow ativo, **When** um cliente solicita o inicio com entrada valida, **Then** recebe
   o identificador da execucao imediatamente, sem esperar o fim.
2. **Given** uma execucao, **When** consultada, **Then** mostra status, nos concluidos e saida quando
   terminar.
3. **Given** o mesmo pedido de inicio repetido com a mesma chave de idempotencia, **When** recebido,
   **Then** devolve a execucao ja criada, sem criar outra.

---

### User Story 2 - Agendamento (Priority: P2)

Um workflow de relatorio diario roda todo dia as 8h, sem ninguem disparar.

**Acceptance Scenarios**:

1. **Given** um agendamento diario as 8h, **When** chega o horario, **Then** uma unica execucao inicia,
   mesmo com varias instancias rodando.
2. **Given** todas as instancias paradas no horario, **When** voltam, **Then** a politica de atraso
   (executar uma vez ou pular) e aplicada.

---

### User Story 3 - Workflow a partir do chat e da fila (Priority: P2)

No chat, o agente pode disparar um workflow publicado como ferramenta; um item da fila de tarefas pode
iniciar um workflow.

**Acceptance Scenarios**:

1. **Given** um workflow publicado como ferramenta, **When** o agente do chat o chama, **Then** a conversa
   recebe o resultado, ou acompanha a execucao se for longa.
2. **Given** um workflow que suspende esperando pessoa, **When** chamado pelo chat, **Then** a conversa
   suspende junto e retoma quando ele termina.

---

### User Story 4 - Fila e prioridade (Priority: P3)

Com muitas execucoes pedidas ao mesmo tempo, o sistema respeita um limite de execucoes simultaneas por
workflow e a prioridade dos pedidos.

**Acceptance Scenarios**:

1. **Given** limite de 2 simultaneas e 5 pedidos, **When** processados, **Then** nunca ha mais de 2 em
   execucao e os demais aguardam na fila.
2. **Given** pedidos de prioridades diferentes na fila, **When** uma vaga abre, **Then** o de maior
   prioridade inicia primeiro.

### Edge Cases

- Inicio de workflow desabilitado ou inexistente: rejeitado com motivo.
- Webhook com assinatura invalida: rejeitado e registrado.
- Agendamento invalido: rejeitado ao salvar.
- Fuso horario: agendamentos declaram o fuso.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST permitir iniciar um workflow por API, retornando o identificador sem esperar
  o fim.
- **FR-002**: O sistema MUST permitir consultar status, progresso e saida de uma execucao, e cancela-la.
- **FR-003**: Inicios MUST aceitar chave de idempotencia.
- **FR-004**: O sistema MUST permitir agendar workflows com expressao de recorrencia e fuso, com execucao
  unica por horario entre instancias.
- **FR-005**: O sistema MUST permitir iniciar workflows por webhook assinado e por tarefa da fila.
- **FR-006**: Um workflow MUST poder ser publicado como ferramenta para agentes, com suspensao propagada a
  conversa.
- **FR-007**: O sistema MUST respeitar limite de execucoes simultaneas por workflow e prioridade.
- **FR-008**: Todo inicio MUST registrar a origem (API, agenda, webhook, fila, chat).

### Key Entities

- **Gatilho**: tipo, configuracao, workflow, ativo.
- **Pedido de execucao**: entrada, prioridade, chave de idempotencia, origem, status na fila.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: O cliente recebe o identificador da execucao em menos de 1 segundo.
- **SC-002**: 0 execucoes duplicadas por agendamento com 2 ou mais instancias.
- **SC-003**: 0 execucoes duplicadas por pedidos repetidos com a mesma chave.
- **SC-004**: O limite de simultaneas nunca e ultrapassado.

## Assumptions

- Depende de 003 (retomada automatica) para execucoes iniciadas por API sobreviverem a quedas.
- A API usa a mesma aplicacao web existente; autenticacao fica para 009.
