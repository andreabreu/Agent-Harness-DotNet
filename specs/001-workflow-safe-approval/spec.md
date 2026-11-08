# Feature Specification: Aprovacao segura em workflows

**Feature Branch**: `001-workflow-safe-approval`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "Workflow executado sem ninguem para responder aprova sozinho toda ferramenta com politica de aprovacao (MCP, run_shell). Padrao seguro: negar. Permitir politica por workflow para liberar ou bloquear ferramentas especificas sem mudar a politica global."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Workflow nao aprova sozinho (Priority: P1)

Um operador dispara um workflow cujo agente decide usar uma ferramenta que exige aprovacao (ex.: executar
um comando no shell ou uma ferramenta MCP). Ninguem esta acompanhando. Hoje a ferramenta roda aprovada
automaticamente; o operador espera que ela seja negada, que o agente receba o motivo e que o workflow
siga ou falhe de forma explicita.

**Why this priority**: fecha uma brecha de governanca com risco real (efeito sem consentimento) e custo
minimo.

**Independent Test**: rodar um workflow com um no de agente que tenta uma ferramenta de aprovacao, sem
nenhuma configuracao extra, e verificar que ela nao executou e que a negacao ficou registrada.

**Acceptance Scenarios**:

1. **Given** um workflow cujo agente chama uma ferramenta de politica "aprovacao", **When** o workflow
   roda sem modo de aprovacao informado, **Then** a chamada e negada, a ferramenta nao executa e o agente
   recebe o motivo "negado por padrao em workflow".
2. **Given** o mesmo workflow, **When** o operador informa explicitamente o modo "aprovar tudo",
   **Then** a ferramenta executa e o registro diz que a aprovacao veio desse modo explicito.
3. **Given** uma ferramenta de politica "automatica", **When** o workflow roda, **Then** ela executa
   normalmente.

---

### User Story 2 - Politica por workflow (Priority: P2)

Quem desenha um workflow quer liberar uma ferramenta especifica (ex.: leitura de arquivos via MCP) sem
liberar todas, ou bloquear uma ferramenta que a politica global permite, so naquele workflow.

**Why this priority**: torna o padrao seguro utilizavel sem abrir tudo.

**Independent Test**: definir no workflow "liberar ferramenta X" e "bloquear ferramenta Y" e verificar
que X executa sem pedido, Y e bloqueada e as demais seguem a politica global.

**Acceptance Scenarios**:

1. **Given** um workflow que libera a ferramenta X, **When** o agente chama X, **Then** X executa sem
   pedido e o registro cita a regra do workflow.
2. **Given** um workflow que bloqueia a ferramenta Y (global "automatica"), **When** o agente chama Y,
   **Then** Y e bloqueada.
3. **Given** uma regra de bloqueio por argumento global (ex.: comando destrutivo), **When** o workflow
   libera a ferramenta, **Then** o bloqueio por argumento continua valendo.

### Edge Cases

- Regra do workflow citando ferramenta inexistente: o workflow nao inicia e informa a ferramenta.
- Workflow que libera e bloqueia a mesma ferramenta: bloqueio vence.
- Padroes com curinga (ex.: todas as ferramentas de um servidor MCP) devem ser aceitos.
- Subagentes chamados por um no herdam a politica do workflow.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Sem modo de aprovacao informado, a execucao de workflow MUST negar ferramentas de politica
  "aprovacao".
- **FR-002**: O operador MUST poder escolher explicitamente outro modo ao disparar o workflow.
- **FR-003**: A definicao do workflow MUST aceitar listas de ferramentas liberadas e bloqueadas, com
  curinga.
- **FR-004**: Bloqueio MUST prevalecer sobre liberacao; regras de bloqueio por argumento MUST sempre valer.
- **FR-005**: A politica do workflow MUST valer para o agente do no e para seus subagentes.
- **FR-006**: Cada decisao MUST ser registrada com a origem (padrao, modo explicito, regra do workflow,
  politica global).
- **FR-007**: Os modos de chat e fila MUST manter o comportamento atual.

### Key Entities

- **Politica de workflow**: listas de ferramentas liberadas e bloqueadas, parte da definicao versionada.
- **Decisao de politica**: ferramenta, resultado e origem da decisao, ligada a execucao.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 0 execucoes de ferramenta de politica "aprovacao" sem consentimento explicito nos cenarios
  de teste de workflow.
- **SC-002**: 100% das decisoes de politica em workflow mostram a origem no registro.
- **SC-003**: Todos os cenarios e testes ja existentes continuam passando.

## Assumptions

- A espera humana duravel dentro de nos fica para a feature 004; ate la, "negar" e o unico padrao seguro.
- O modo explicito continua disponivel para ambientes de desenvolvimento.
