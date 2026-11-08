# Feature Specification: Observabilidade e telas de workflow

**Feature Branch**: `008-workflow-observability-ui`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "Linha do tempo por run (nos, duracao, tentativas, tokens, custo); eventos de workflow ao vivo para a tela; replay parcial e reexecucao de run falho; editor visual do grafo; lista de execucoes ao vivo."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Acompanhar uma execucao ao vivo (Priority: P1)

O operador abre uma execucao e ve o grafo com o status de cada no mudando em tempo real: em execucao,
concluido, pulado, falho, esperando pessoa; ao clicar num no, ve entrada, saida, tentativas, duracao,
tokens e custo.

**Why this priority**: sem visibilidade, ninguem confia num fluxo autonomo.

**Independent Test**: abrir uma execucao em andamento e ver as mudancas de status sem recarregar a
pagina, inclusive quando outra instancia executa os nos.

**Acceptance Scenarios**:

1. **Given** uma execucao em andamento, **When** um no muda de status, **Then** a tela reflete em ate 2
   segundos.
2. **Given** a tela fechada e reaberta, **When** carrega, **Then** mostra o estado completo, sem perder
   eventos.
3. **Given** um no de agente, **When** selecionado, **Then** mostra os passos do agente (ferramentas,
   subagentes, decisoes humanas).
4. **Given** a execucao terminou, **When** consultada, **Then** mostra duracao total, tokens e custo por
   no e total.

---

### User Story 2 - Lista de execucoes (Priority: P2)

O operador ve todas as execucoes com filtro por workflow, status e periodo, e encontra rapidamente as que
falharam ou esperam alguem.

**Acceptance Scenarios**:

1. **Given** execucoes de varios status, **When** filtradas por "esperando pessoa", **Then** so elas
   aparecem, com quem precisa decidir.
2. **Given** a lista aberta, **When** uma execucao muda de status, **Then** a lista atualiza sozinha.

---

### User Story 3 - Reexecutar a partir de um no (Priority: P2)

Uma execucao falhou no no 4 por um problema ja corrigido. O operador reexecuta a partir do no 4,
aproveitando as saidas dos nos 1 a 3.

**Acceptance Scenarios**:

1. **Given** uma execucao falha, **When** o operador reexecuta a partir de um no, **Then** cria uma nova
   execucao ligada a original, reaproveitando as saidas anteriores a esse no.
2. **Given** a reexecucao inclui nos com efeito ja realizados, **When** solicitada, **Then** o operador e
   avisado de quais efeitos se repetirao e confirma.

---

### User Story 4 - Editor visual (Priority: P3)

Quem desenha monta o workflow arrastando nos, ligando arestas e preenchendo propriedades por tipo, com
validacao antes de salvar.

**Acceptance Scenarios**:

1. **Given** o editor, **When** um no e adicionado, **Then** o formulario mostra so as propriedades do
   tipo dele.
2. **Given** uma definicao invalida, **When** salva, **Then** os erros aparecem no no correspondente.
3. **Given** uma definicao salva no editor, **When** aberta como texto, **Then** e equivalente; e o texto
   editado abre no editor.

### Edge Cases

- Execucao com centenas de nos (iteracao): a tela agrupa itens e continua responsiva.
- Evento perdido pela conexao: reconexao retoma do ultimo evento recebido.
- Custo desconhecido para um modelo: mostrar tokens e "custo indisponivel".

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O motor MUST registrar eventos por no (iniciado, concluido, falho, pulado, ramo escolhido,
  tentativa, esperando, retomado, cancelado).
- **FR-002**: A tela MUST receber esses eventos ao vivo, com reconexao sem perda.
- **FR-003**: Cada execucao MUST ter linha do tempo com duracao, tentativas, tokens e custo por no e total.
- **FR-004**: MUST existir uma lista de execucoes com filtros e atualizacao ao vivo.
- **FR-005**: O operador MUST poder reexecutar uma execucao ou reexecutar a partir de um no, com aviso de
  efeitos repetidos.
- **FR-006**: MUST existir um editor visual com validacao, equivalente a definicao em texto.

### Key Entities

- **Evento de workflow**: execucao, no, tipo, dados, momento, sequencia.
- **Custo**: tokens de entrada e saida por modelo e preco por modelo.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Mudancas de status aparecem na tela em ate 2 segundos.
- **SC-002**: 0 eventos perdidos apos reconexao.
- **SC-003**: Um operador localiza a causa de uma falha (no e erro) em menos de 1 minuto.
- **SC-004**: Um workflow de 5 nos com desvio e montado no editor sem editar texto.

## Assumptions

- Depende de 002 e 003 para os status pulado, tentativa e recuperacao.
- O front segue a decisao de tecnologia de tela do projeto (a escolha entre a tela atual e um front
  separado fica para o plano).
- O preco por modelo e cadastrado pelo administrador.
