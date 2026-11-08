# Feature Specification: Integracao MCP confiavel

**Feature Branch**: `005-mcp-integration-hardening`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "Transporte HTTP para servidores MCP remotos com autenticacao; sync automatico (subida, periodico, list_changed); side_effect e politica a partir das anotacoes readOnlyHint/destructiveHint; reinicio do processo e timeout por chamada; remover ferramentas que sumiram; tela de cadastro de MCP e associacao a agentes; allowed_tools dos especialistas."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Ferramentas MCP classificadas corretamente (Priority: P1)

Um administrador conecta um servidor MCP. As ferramentas de leitura entram sem efeito e com politica
automatica; as destrutivas entram com aprovacao. Hoje todas entram como "com efeito e com aprovacao", e
leituras interrompidas viram pergunta humana sem necessidade.

**Why this priority**: corrige governanca e experiencia de todas as ferramentas MCP de uma vez.

**Independent Test**: sincronizar um servidor de teste com ferramentas anotadas como leitura e como
destrutiva e verificar a classificacao de cada uma.

**Acceptance Scenarios**:

1. **Given** uma ferramenta anotada como so leitura, **When** sincronizada, **Then** fica sem efeito e
   com politica automatica.
2. **Given** uma ferramenta anotada como destrutiva ou sem anotacao, **When** sincronizada, **Then** fica
   com efeito e com aprovacao.
3. **Given** um administrador ajustou a politica de uma ferramenta, **When** o servidor e sincronizado de
   novo, **Then** o ajuste manual e preservado.

---

### User Story 2 - Servidores MCP remotos (Priority: P2)

O administrador cadastra um servidor MCP acessado pela rede, com credencial, sem processo local.

**Acceptance Scenarios**:

1. **Given** um servidor remoto com credencial valida, **When** sincronizado, **Then** suas ferramentas
   aparecem no registro e podem ser chamadas.
2. **Given** credencial invalida, **When** sincronizado, **Then** o erro aparece no cadastro e nenhuma
   ferramenta e alterada.
3. **Given** uma credencial cadastrada, **When** alguem consulta o cadastro, **Then** ela nunca aparece em
   texto claro.

---

### User Story 3 - Catalogo sempre atualizado e servidores saudaveis (Priority: P2)

O catalogo acompanha os servidores sem comando manual; servidores que caem voltam sozinhos; chamadas
travadas expiram.

**Acceptance Scenarios**:

1. **Given** um servidor habilitado, **When** a aplicacao sobe, **Then** suas ferramentas sao
   sincronizadas.
2. **Given** o servidor avisa que a lista mudou, **When** o aviso chega, **Then** o catalogo e atualizado.
3. **Given** uma ferramenta deixou de existir, **When** sincronizado, **Then** ela fica indisponivel para
   os agentes e o historico continua consultavel.
4. **Given** o processo local do servidor morreu, **When** uma ferramenta e chamada, **Then** o servidor e
   reiniciado e a chamada prossegue.
5. **Given** uma chamada excede o prazo, **When** vence, **Then** falha com motivo claro.

---

### User Story 4 - Cadastro na tela e associacao a agentes (Priority: P3)

O administrador liga e desliga servidores, ve as ferramentas, ajusta politicas e define quais agentes
podem usar quais ferramentas MCP.

**Acceptance Scenarios**:

1. **Given** a tela de MCP, **When** o administrador cadastra e habilita um servidor, **Then** a
   sincronizacao roda e o resultado aparece.
2. **Given** um especialista sem acesso a MCP, **When** o administrador libera um servidor para ele,
   **Then** o especialista passa a descobrir essas ferramentas.

### Edge Cases

- Duas instancias sincronizando ao mesmo tempo: o resultado e o mesmo de uma sincronizacao.
- Nome de ferramenta que colide entre servidores: prefixo do servidor garante unicidade.
- Servidor indisponivel na subida: a aplicacao sobe e registra o aviso.
- Anotacoes contraditorias (leitura e destrutiva): trata como destrutiva.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A sincronizacao MUST derivar efeito e politica das anotacoes da ferramenta, com padrao
  seguro (com efeito e com aprovacao) quando ausentes ou contraditorias.
- **FR-002**: Ajustes manuais de politica e efeito MUST sobreviver a novas sincronizacoes.
- **FR-003**: O sistema MUST suportar servidores MCP remotos pela rede, alem dos locais.
- **FR-004**: Credenciais MUST ser guardadas sem exposicao em texto claro em telas, logs ou registros.
- **FR-005**: A sincronizacao MUST ocorrer na subida, periodicamente e ao aviso de mudanca do servidor.
- **FR-006**: Ferramentas removidas pelo servidor MUST ficar indisponiveis para os agentes.
- **FR-007**: Servidores locais MUST ser reiniciados quando morrem; chamadas MUST ter prazo.
- **FR-008**: MUST existir uma tela para cadastrar servidores, ver ferramentas, ajustar politicas e
  associar a agentes.
- **FR-009**: Os especialistas do seed MUST receber acesso as ferramentas MCP adequadas ao papel de cada um.

### Key Entities

- **Servidor MCP**: tipo de conexao (local ou remota), endereco ou comando, credencial, estado, ultima
  sincronizacao.
- **Ferramenta MCP**: anotacoes de origem, efeito, politica, origem do ajuste (sincronizacao ou manual),
  disponibilidade.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% das ferramentas anotadas como so leitura entram sem efeito e sem pedir aprovacao.
- **SC-002**: 0 credenciais visiveis em telas, logs ou registros.
- **SC-003**: Mudancas no servidor aparecem no catalogo em ate 1 minuto.
- **SC-004**: Um servidor local que caiu volta a atender na chamada seguinte.

## Assumptions

- Remoto significa o transporte HTTP padrao do protocolo MCP.
- O servidor de teste usado na prova e local e controlado pelo projeto, sem depender de internet.
- Permissao de acesso a tela de administracao depende de autenticacao (feature 009).
