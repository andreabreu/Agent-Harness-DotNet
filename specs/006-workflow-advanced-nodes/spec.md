# Feature Specification: Nos avancados de workflow

**Feature Branch**: `006-workflow-advanced-nodes`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "Nos map/foreach, loop/while com teto, subworkflow, wait/timer duravel, signal/webhook para eventos externos e http direto."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Processar uma lista (Priority: P1)

Um workflow recebe uma lista (ex.: 50 contratos) e precisa rodar a mesma analise em cada item, com limite
de paralelismo, juntando os resultados.

**Why this priority**: e o padrao mais comum de automacao em lote.

**Independent Test**: lista de 10 itens com paralelismo 3; 10 resultados na ordem dos itens, nunca mais
de 3 ao mesmo tempo.

**Acceptance Scenarios**:

1. **Given** uma lista de N itens, **When** o no de iteracao executa, **Then** produz N resultados na
   ordem da lista.
2. **Given** limite de paralelismo 3, **When** executa, **Then** nunca ha mais de 3 itens em execucao.
3. **Given** uma queda no meio, **When** retomado, **Then** so os itens nao concluidos executam.
4. **Given** um item falha, **When** a politica e "continuar", **Then** os outros concluem e a falha
   aparece no resultado do item.

---

### User Story 2 - Esperar tempo ou evento externo (Priority: P1)

Um workflow precisa esperar ate uma data, ou ate um sistema externo avisar (ex.: pagamento confirmado),
sem segurar processo por horas ou dias.

**Acceptance Scenarios**:

1. **Given** um no de espera de 2 horas, **When** alcancado, **Then** o workflow fica esperando sem
   processo e continua apos o prazo, mesmo com reinicios no meio.
2. **Given** um no que espera um evento com uma chave, **When** o evento chega com essa chave e dados,
   **Then** o workflow continua e os dados ficam disponiveis.
3. **Given** um evento com chave desconhecida ou ja consumida, **When** chega, **Then** e rejeitado e
   registrado.
4. **Given** prazo maximo da espera vencido, **When** a varredura passa, **Then** segue o ramo de
   expiracao.

---

### User Story 3 - Repetir ate dar certo (Priority: P2)

Um fluxo de revisao repete "escrever → revisar" ate o revisor aprovar, com teto de iteracoes.

**Acceptance Scenarios**:

1. **Given** um laco com condicao de saida e teto 5, **When** a condicao e satisfeita na 3a iteracao,
   **Then** o laco termina com 3 iteracoes registradas.
2. **Given** a condicao nunca e satisfeita, **When** atinge o teto, **Then** segue o ramo "teto atingido".

---

### User Story 4 - Reusar workflows e chamar APIs (Priority: P3)

Quem desenha quer chamar outro workflow como um passo e fazer uma chamada HTTP direta sem cadastrar uma
ferramenta.

**Acceptance Scenarios**:

1. **Given** um no que chama outro workflow, **When** executa, **Then** o filho roda com sua propria
   versao fixa e a saida dele volta ao pai.
2. **Given** o filho suspenso (esperando pessoa), **When** suspenso, **Then** o pai tambem fica suspenso
   e retoma quando o filho termina.
3. **Given** um no HTTP, **When** executa, **Then** a resposta (status e corpo) fica disponivel e
   destinos internos seguem as mesmas regras de aprovacao das ferramentas HTTP.

### Edge Cases

- Lista vazia: o no conclui com resultado vazio.
- Lista muito grande: limite maximo de itens configuravel; acima dele, falha clara.
- Recursao de subworkflow (A chama A): rejeitada na validacao ou com teto de profundidade.
- Evento que chega antes de o workflow chegar ao no de espera: guardado e entregue ao chegar.
- Chamada HTTP com efeito interrompida: tratada como ferramenta com efeito (Principio II).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST ter um no de iteracao sobre listas, com limite de paralelismo, ordem
  preservada, checkpoint por item e politica de falha por item.
- **FR-002**: O sistema MUST ter um laco com condicao de saida e teto obrigatorio.
- **FR-003**: O sistema MUST ter um no que chama outro workflow, com propagacao de suspensao, cancelamento
  e resultado.
- **FR-004**: O sistema MUST ter um no de espera por duracao ou data, duravel.
- **FR-005**: O sistema MUST ter um no de espera por evento externo identificado por chave, com prazo
  maximo.
- **FR-006**: O sistema MUST aceitar eventos externos por um ponto de entrada autenticado.
- **FR-007**: O sistema MUST ter um no de chamada HTTP direta, sujeito as politicas de destino.
- **FR-008**: Todos os nos novos MUST respeitar retomada, versao fixa e efeito no maximo uma vez.

### Key Entities

- **Iteracao**: lista de entrada, item atual, resultados por item.
- **Espera**: tipo (tempo ou evento), chave, prazo, status.
- **Evento externo**: chave, dados, recebido em, consumido por.
- **Chamada de subworkflow**: pai, filho, status, resultado.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Em lista de 100 itens com queda no meio, 0 itens concluidos reexecutados.
- **SC-002**: Esperas de horas ou dias nao ocupam processo e continuam no horario com ate 1 minuto de
  atraso.
- **SC-003**: 0 eventos externos entregues duas vezes ou ao workflow errado.
- **SC-004**: 100% dos lacos param no teto.

## Assumptions

- Depende de 002 (condicoes e campos) e 003 (retomada automatica e versao fixa).
- Limite maximo padrao de itens por iteracao: 1000.
- A autenticacao do ponto de entrada de eventos usa um segredo por workflow ate existir 009.
