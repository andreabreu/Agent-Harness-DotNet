# Feature Specification: Governanca de definicoes de workflow

**Feature Branch**: `009-workflow-governance`

**Created**: 2026-11-08

**Status**: Draft

**Input**: User description: "Validacao da definicao ao salvar; historico de versoes com diff e rascunho para ativo; permissoes de quem cria, edita e executa cada workflow; segredos e variaveis por ambiente sem credenciais na definicao."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Definicao valida antes de publicar (Priority: P1)

Quem desenha salva um workflow e recebe todos os erros de uma vez (no inexistente, ciclo sem laco, campo
obrigatorio ausente, agente ou ferramenta inexistente, referencia a campo de no que nao vem antes), em vez
de descobrir no meio da execucao.

**Why this priority**: evita falhas em producao e e pre-requisito do editor e do versionamento.

**Independent Test**: salvar definicoes com cada tipo de erro e verificar a mensagem; salvar uma valida e
verificar que e aceita.

**Acceptance Scenarios**:

1. **Given** uma definicao com tres erros, **When** salva, **Then** e rejeitada listando os tres, cada um
   com o no e o campo.
2. **Given** uma definicao valida, **When** salva, **Then** e aceita.

---

### User Story 2 - Rascunho, publicacao e historico (Priority: P1)

Quem desenha edita um rascunho sem afetar execucoes; ao publicar, uma nova versao passa a valer para novas
execucoes; o historico mostra cada versao, quem publicou e o que mudou.

**Acceptance Scenarios**:

1. **Given** uma versao publicada e um rascunho em edicao, **When** alguem inicia o workflow, **Then** usa a
   versao publicada.
2. **Given** duas versoes, **When** comparadas, **Then** o diff mostra nos e propriedades adicionados,
   removidos e alterados.
3. **Given** uma versao anterior, **When** restaurada, **Then** vira um novo rascunho, sem apagar o
   historico.

---

### User Story 3 - Segredos e variaveis por ambiente (Priority: P2)

Um workflow usa uma URL e uma credencial que diferem entre homologacao e producao, sem que a credencial
apareca na definicao.

**Acceptance Scenarios**:

1. **Given** um segredo referenciado pelo nome, **When** o no executa, **Then** recebe o valor do ambiente
   atual.
2. **Given** a definicao, o historico, os eventos ou a tela, **When** consultados, **Then** o valor do
   segredo nunca aparece.
3. **Given** um segredo referenciado e inexistente no ambiente, **When** o workflow inicia, **Then** e
   rejeitado citando o nome.

---

### User Story 4 - Permissoes (Priority: P3)

Cada workflow define quem pode editar, publicar e executar; agentes chamados por um workflow so usam o que
a pessoa que disparou poderia usar.

**Acceptance Scenarios**:

1. **Given** um usuario sem permissao de publicar, **When** tenta publicar, **Then** e negado.
2. **Given** um usuario com permissao so de executar, **When** abre o workflow, **Then** ve a definicao
   mas nao edita.

### Edge Cases

- Publicacao concorrente por duas pessoas: a segunda recebe conflito e ve a versao nova.
- Remocao de um agente ou ferramenta usado por uma versao publicada: avisado na remocao; a versao continua
  valida para execucoes em andamento.
- Segredo rotacionado com execucoes em andamento: os nos seguintes usam o valor novo.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Salvar uma definicao MUST validar estrutura, tipos de no, campos obrigatorios, referencias a
  nos, campos, agentes, ferramentas e segredos, e ciclos fora de lacos.
- **FR-002**: Todos os erros MUST ser devolvidos de uma vez, com localizacao.
- **FR-003**: Workflows MUST ter rascunho e versoes publicadas imutaveis; so a publicada inicia execucoes.
- **FR-004**: O historico MUST mostrar autor, data e diff entre versoes, e permitir restaurar.
- **FR-005**: Definicoes MUST referenciar segredos e variaveis por nome; valores MUST variar por ambiente e
  nunca ser expostos.
- **FR-006**: O sistema MUST controlar permissoes de editar, publicar e executar por workflow.
- **FR-007**: Toda publicacao, restauracao e mudanca de permissao MUST ser registrada.

### Key Entities

- **Versao de workflow**: numero, definicao, status (rascunho, publicada), autor, data.
- **Segredo ou variavel**: nome, ambiente, valor protegido.
- **Permissao**: usuario ou grupo, workflow, acao.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% dos erros de definicao detectaveis estaticamente sao barrados ao salvar.
- **SC-002**: 0 valores de segredo visiveis em definicoes, historicos, eventos ou telas.
- **SC-003**: Qualquer versao publicada pode ser comparada e restaurada em menos de 1 minuto.

## Assumptions

- As permissoes (User Story 4) dependem de autenticacao de usuarios, que o projeto ainda nao tem; ela entra
  como parte desta feature, com um provedor simples para on-prem e extensivel para SaaS.
- O ambiente da instancia e definido por configuracao.
