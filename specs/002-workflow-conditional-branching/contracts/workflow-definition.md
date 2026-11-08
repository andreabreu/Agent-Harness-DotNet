# Contrato: definicao de workflow (v2)

Formato completo e regras por campo: [data-model.md](../data-model.md#definicao-do-workflow-workflowsdefinition).

## Compatibilidade

- Definicoes atuais (so `agent`/`tool`, `next`, `{{no}}`, `{{input}}`) continuam validas e com o mesmo resultado.
- Mudanca intencional: `{{x}}` para no inexistente passa a ser erro de definicao (antes virava texto vazio).

## Erros de definicao

Detectados antes de criar `workflow_runs`; o workflow nao inicia e a mensagem lista todos, um por linha, no
formato `<no>.<campo>: <problema>`, por exemplo:

```text
Definicao invalida do workflow 'triagem':
rotear.cases[1].next: destino 'juridco' nao existe
classificar.output_schema.properties.score: palavra-chave 'minimum' nao suportada
consolidar.join: 'some' invalido (use all, all_active, any)
```

## Erros de execucao

| Situacao | Efeito |
|---|---|
| saida do agente fora do `output_schema` apos as tentativas | no `failed`, saida = erros da validacao da ultima tentativa |
| referencia a campo inexistente (`{{classificar.cor}}`) | no que referencia `failed`, citando o caminho |
| entrada fora do `input_schema` | workflow nao inicia; erros listados |
| `end` com `status: failed` | workflow `failed`, saida = `output` renderizado |
