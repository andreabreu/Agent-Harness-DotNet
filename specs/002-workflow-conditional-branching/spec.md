# Feature Specification: Desvio condicional e dados estruturados em workflows

**Feature Branch**: `002-workflow-conditional-branching`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "No condition deterministico; agente classifica com saida estruturada e o no de desvio decide o caminho. Acesso a campos da saida dos nos, status skipped para ramos nao escolhidos, tipos de join, escolha gravada no checkpoint, entrada tipada do workflow, saida final configuravel, nos transform e fail/end."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Rotear por classificacao (Priority: P1)

Quem desenha um workflow de triagem quer que um agente classifique o pedido em categorias fechadas
(ex.: juridico, financeiro, suporte; urgente sim/nao) e que o fluxo siga o ramo certo por uma regra
visivel no desenho, nao por instrucao no prompt.

**Why this priority**: e o recurso que transforma o motor de um grafo fixo num fluxo de negocio.

**Independent Test**: um workflow com classificador e desvio; entradas diferentes seguem ramos
diferentes, e os ramos nao escolhidos aparecem como pulados.

**Acceptance Scenarios**:

1. **Given** um classificador com categorias fechadas e um desvio com casos e um ramo padrao, **When** a
   classificacao e "juridico", **Then** so o ramo juridico executa e os outros ficam como pulados.
2. **Given** nenhum caso satisfeito, **When** o desvio avalia, **Then** segue o ramo padrao.
3. **Given** o classificador responde fora das categorias permitidas, **When** a saida e validada,
   **Then** o agente e chamado de novo ate o limite e, se ainda invalida, o no falha com o motivo.

---

### User Story 2 - Retomar sem recalcular a decisao (Priority: P1)

Um workflow cai depois do desvio. Na retomada, o caminho escolhido MUST ser o mesmo, mesmo que o
classificador responderia diferente agora.

**Why this priority**: sem isso, o desvio quebra a durabilidade (Principio I).

**Independent Test**: queda simulada apos o desvio; a retomada segue o mesmo ramo e nao chama o
classificador de novo.

**Acceptance Scenarios**:

1. **Given** um desvio ja avaliado e gravado, **When** o workflow e retomado, **Then** o mesmo ramo segue
   e o classificador nao executa de novo.

---

### User Story 3 - Juntar ramos com pulados (Priority: P2)

Depois de ramos alternativos, um no de consolidacao precisa rodar quando o ramo escolhido termina, sem
ficar esperando os ramos pulados.

**Independent Test**: dois ramos alternativos convergindo num no; ele executa apos o ramo escolhido.

**Acceptance Scenarios**:

1. **Given** um no que junta ramos com o modo "todos os nao pulados", **When** um ramo foi pulado,
   **Then** o no executa apos o outro terminar.
2. **Given** o modo "qualquer um", **When** o primeiro antecessor termina, **Then** o no executa uma
   unica vez.
3. **Given** todos os antecessores pulados, **When** o join avalia, **Then** o no tambem e pulado.

---

### User Story 4 - Dados estruturados entre nos (Priority: P2)

Quem desenha quer usar campos especificos da saida de um no (ex.: `classificar.categoria`, o primeiro
item de uma lista), validar a entrada do workflow, ajustar dados sem chamar modelo e definir qual e a
saida final.

**Independent Test**: um workflow com entrada validada, um no de transformacao e uma saida final
escolhida.

**Acceptance Scenarios**:

1. **Given** uma entrada que nao segue o formato declarado, **When** o workflow e disparado, **Then** nao
   inicia e informa os campos invalidos.
2. **Given** referencia a um campo da saida de um no, **When** o no seguinte executa, **Then** recebe o
   valor do campo.
3. **Given** um no "fim com erro" num ramo, **When** ele e alcancado, **Then** o workflow termina como
   falho com a mensagem definida.

### Edge Cases

- Referencia a campo inexistente: falha clara no no que referencia, citando o caminho.
- Condicao invalida na definicao: rejeitada ao salvar ou ao iniciar.
- Pulados se propagam ate onde nenhum antecessor executou.
- Saida textual continua aceita (compatibilidade com os workflows atuais).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST ter um no de desvio com casos ordenados (primeiro que satisfaz vence) e ramo
  padrao.
- **FR-002**: As condicoes MUST aceitar comparacoes (igual, diferente, maior, menor, contem, existe) e
  combinacoes (e, ou, nao) sobre campos de nos anteriores e da entrada.
- **FR-003**: Um no de agente MUST poder declarar o formato da saida (campos, tipos, valores permitidos);
  saida fora do formato MUST gerar nova tentativa ate um limite e depois falha.
- **FR-004**: O ramo escolhido MUST ser gravado junto com o checkpoint e reutilizado na retomada.
- **FR-005**: Nos nao escolhidos MUST ficar com status "pulado", propagado aos dependentes exclusivos.
- **FR-006**: Joins MUST suportar "todos", "qualquer um" e "todos os nao pulados"; "todos" continua padrao.
- **FR-007**: Referencias MUST suportar campos aninhados e itens de lista.
- **FR-008**: O workflow MUST poder declarar e validar o formato da entrada.
- **FR-009**: O sistema MUST ter nos de transformacao (sem modelo), fim com sucesso e fim com erro.
- **FR-010**: A saida final do workflow MUST ser configuravel; sem configuracao, mantem o comportamento
  atual.
- **FR-011**: Workflows existentes MUST continuar funcionando sem alteracao.

### Key Entities

- **No de desvio**: casos (condicao, destino) e ramo padrao.
- **Formato de saida**: campos, tipos e valores permitidos de um no.
- **Registro de decisao**: ramo escolhido por um desvio numa execucao.
- **Status de no**: inclui "pulado".

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Em 100% das retomadas apos o desvio, o ramo seguido e o mesmo da primeira execucao.
- **SC-002**: 0 nos executados em ramos nao escolhidos.
- **SC-003**: Saidas fora do formato nunca seguem adiante (100% viram nova tentativa ou falha).
- **SC-004**: Um workflow de triagem com 3 categorias pode ser descrito so com definicao, sem codigo.

## Assumptions

- O limite padrao de novas tentativas de formato e 2.
- A linguagem de condicoes e pequena e propria (sem execucao de codigo arbitrario).
