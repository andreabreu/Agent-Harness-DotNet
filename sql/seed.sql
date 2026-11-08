-- ============================================================
-- AgentPoc - seed. Idempotente (upsert). Edite aqui ou direto no banco.
-- ============================================================

-- ---------------- provedores ----------------
insert into providers (id, kind, base_url, api_key_env, auth_header) values
 ('azure_foundry', 'openai_compat', '${AZURE_FOUNDRY_ENDPOINT}', 'AZURE_FOUNDRY_API_KEY', 'api-key'),
 ('anthropic',     'anthropic',     'https://api.anthropic.com', 'ANTHROPIC_API_KEY',     'x-api-key'),
 ('foundry_local', 'openai_compat', '${FOUNDRY_LOCAL_ENDPOINT}', null,                    'none'),
 ('ollama',        'openai_compat', 'http://localhost:11434/v1', null,                    'none')
on conflict (id) do update set kind = excluded.kind, base_url = excluded.base_url,
  api_key_env = excluded.api_key_env, auth_header = excluded.auth_header;

-- ---------------- modelos ----------------
insert into models (id, provider_id, model_name, supports_tools, supports_temperature, max_tokens_param,
                    max_output_tokens, context_window, cost_in_per_mtok, cost_out_per_mtok) values
 ('foundry_default', 'azure_foundry', '${AZURE_FOUNDRY_DEPLOYMENT}', true, false, 'max_completion_tokens', 4096, 128000, 2.50, 10.00),
 ('claude_default',  'anthropic',     '${ANTHROPIC_MODEL}',          true, true,  'max_tokens',            4096, 200000, 3.00, 15.00),
 ('local_default',   'foundry_local', '${FOUNDRY_LOCAL_MODEL}',      true, true,  'max_tokens',            2048,  32000, 0, 0),
 ('ollama_default',  'ollama',        'qwen2.5:7b',                  true, true,  'max_tokens',            2048,  32000, 0, 0)
on conflict (id) do update set provider_id = excluded.provider_id, model_name = excluded.model_name,
  supports_tools = excluded.supports_tools, supports_temperature = excluded.supports_temperature,
  max_tokens_param = excluded.max_tokens_param, max_output_tokens = excluded.max_output_tokens,
  context_window = excluded.context_window, cost_in_per_mtok = excluded.cost_in_per_mtok,
  cost_out_per_mtok = excluded.cost_out_per_mtok;

-- ---------------- tools ----------------
insert into tools (name, description, kind, parameters, policy, config, keywords) values
 ('search_tools', 'Procura ferramentas disponiveis no registro por palavras-chave e as carrega para uso imediato. Use sempre que precisar de uma capacidade que ainda nao tem.', 'builtin',
  '{"type":"object","properties":{"query":{"type":"string","description":"palavras-chave da capacidade desejada"}},"required":["query"]}', 'auto', '{}', 'discovery buscar ferramenta capacidade'),
 ('list_agents', 'Lista os agentes especialistas para os quais voce pode delegar, com suas descricoes.', 'builtin',
  '{"type":"object","properties":{}}', 'auto', '{}', 'agentes equipe especialistas delegar'),
 ('delegate_task', 'Delega uma subtarefa a um agente especialista e espera o resultado (sincrono). O especialista tem contexto limpo: passe todo o contexto necessario na tarefa.', 'builtin',
  '{"type":"object","properties":{"agent":{"type":"string","description":"id do agente"},"task":{"type":"string","description":"tarefa completa e autocontida"}},"required":["agent","task"]}', 'auto', '{}', 'delegar subagente especialista sincrono'),
 ('create_task', 'Cria uma tarefa assincrona na fila para um agente (nao espera). Retorna o id da tarefa.', 'builtin',
  '{"type":"object","properties":{"agent":{"type":"string"},"task":{"type":"string"},"priority":{"type":"integer"}},"required":["agent","task"]}', 'auto', '{}', 'fila ticket assincrono background delegar'),
 ('check_task', 'Consulta status e resultado de uma tarefa assincrona pelo id.', 'builtin',
  '{"type":"object","properties":{"task_id":{"type":"string"}},"required":["task_id"]}', 'auto', '{}', 'fila ticket status resultado'),
 ('memory_save', 'Salva um fato duravel na memoria de longo prazo do agente.', 'builtin',
  '{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}', 'auto', '{}', 'memoria lembrar salvar'),
 ('memory_recall', 'Busca na memoria de longo prazo do agente por palavras-chave.', 'builtin',
  '{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}', 'auto', '{}', 'memoria lembrar recuperar'),
 ('kb_search', 'Busca documentos na base de conhecimento interna (politicas, arquitetura, operacao).', 'builtin',
  '{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer"}},"required":["query"]}', 'auto', '{}', 'rag conhecimento documentos interno base politica arquitetura'),
 ('kb_add', 'Adiciona um documento a base de conhecimento interna.', 'builtin',
  '{"type":"object","properties":{"title":{"type":"string"},"content":{"type":"string"}},"required":["title","content"]}', 'auto', '{}', 'rag conhecimento documento adicionar'),
 ('web_search', 'Pesquisa na internet e devolve titulo, URL e trecho dos melhores resultados. Use SEMPRE que a pergunta envolver fatos recentes, versoes, precos, noticias, documentacao ou qualquer informacao que possa ter mudado; nao responda de memoria nesses casos. Depois leia as fontes mais relevantes com http_get.', 'builtin',
  '{"type":"object","properties":{"query":{"type":"string","description":"consulta de busca, de preferencia com termos especificos"},"limit":{"type":"integer","description":"quantidade de resultados (1-10, padrao 5)"}},"required":["query"]}', 'auto', '{}', 'web internet pesquisa buscar busca google noticias atual recente fonte'),
 ('ask_human', 'Faz uma pergunta a pessoa responsavel e espera a resposta (pode levar minutos ou dias; o trabalho fica pausado sem custo). Use no meio de uma tarefa quando faltar uma informacao que so a pessoa tem, ou antes de um passo irreversivel em que a escolha muda o resultado. Nao use para confirmar o obvio.', 'builtin',
  '{"type":"object","properties":{"question":{"type":"string","description":"pergunta curta e objetiva"},"options":{"type":"array","items":{"type":"string"},"description":"opcoes sugeridas (opcional)"}},"required":["question"]}', 'auto', '{}', 'perguntar humano pessoa usuario confirmar duvida decisao esclarecer'),
 ('think', 'Espaco para raciocinar antes de agir: analise o pedido, resultados inesperados de ferramentas ou decida o proximo passo. Nao executa nada e nao e visto como resposta ao usuario.', 'builtin',
  '{"type":"object","properties":{"thought":{"type":"string","description":"o raciocinio"}},"required":["thought"]}', 'auto', '{}', 'pensar raciocinar refletir analisar'),
 ('update_plan', 'Registra ou atualiza o plano da tarefa atual como uma lista de passos com status. Use em tarefas com mais de dois passos e atualize o status conforme avanca; o usuario acompanha o plano em tempo real.', 'builtin',
  '{"type":"object","properties":{"steps":{"type":"array","items":{"type":"object","properties":{"step":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","done"]}},"required":["step","status"]}}},"required":["steps"]}', 'auto', '{}', 'plano planejar passos tarefas todo progresso'),
 ('http_get', 'Baixa uma pagina web ou API publica e devolve o texto (HTML limpo). Use para pesquisa e scraping simples.', 'builtin',
  '{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}', 'auto',
  '{"approval_when":{"url":"^https?://(localhost|127\\.|0\\.0\\.0\\.0|10\\.|192\\.168\\.|172\\.(1[6-9]|2[0-9]|3[01])\\.|169\\.254\\.|\\[::1\\]|[^/]*\\.(internal|local)(:|/|$))"}}',
  'web internet scraping pagina url pesquisa site'),
 ('read_file', 'Le um arquivo de texto do workspace do agente.', 'builtin',
  '{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}', 'auto', '{}', 'arquivo ler file workspace'),
 ('write_file', 'Cria ou sobrescreve um arquivo de texto no workspace do agente.', 'builtin',
  '{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}', 'auto', '{}', 'arquivo escrever salvar file workspace relatorio script'),
 ('list_files', 'Lista os arquivos do workspace do agente.', 'builtin',
  '{"type":"object","properties":{"path":{"type":"string"}}}', 'auto', '{}', 'arquivo listar diretorio workspace'),
 ('run_shell', 'Executa um comando de shell no workspace (ex.: python3 script.py). Timeout de 30s. Requer aprovacao.', 'builtin',
  '{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}', 'approval',
  '{"deny_when":{"command":"(^|[;&|`(\\s])(sudo|rm\\s+-[a-z]*[rf]|mkfs|shutdown|reboot|dd\\s+if=|chmod\\s+-R\\s+777)|(curl|wget)[^|]*\\|\\s*(ba|z)?sh"}}',
  'shell terminal executar codigo python script comando rodar bash'),
 ('delete_file', 'Apaga um arquivo do workspace.', 'builtin',
  '{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}', 'deny', '{}', 'arquivo apagar remover deletar'),
 ('weather_forecast', 'Condicoes do tempo atuais para uma latitude/longitude (Open-Meteo). Ferramenta HTTP definida so no banco.', 'http',
  '{"type":"object","properties":{"latitude":{"type":"number"},"longitude":{"type":"number"}},"required":["latitude","longitude"]}', 'auto',
  '{"method":"GET","url":"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,relative_humidity_2m,wind_speed_10m"}',
  'clima tempo previsao temperatura weather'),
 ('cep_lookup', 'Consulta endereco brasileiro por CEP (BrasilAPI). Ferramenta HTTP definida so no banco.', 'http',
  '{"type":"object","properties":{"cep":{"type":"string","description":"CEP so com numeros"}},"required":["cep"]}', 'auto',
  '{"method":"GET","url":"https://brasilapi.com.br/api/cep/v1/{cep}"}',
  'cep endereco brasil logradouro')
on conflict (name) do update set description = excluded.description, kind = excluded.kind,
  parameters = excluded.parameters, policy = excluded.policy, config = excluded.config, keywords = excluded.keywords;

-- ---------------- MCP (desligado por padrao; precisa de Node no host) ----------------
insert into mcp_servers (id, command, args, enabled) values
 ('filesystem', 'npx', '{-y,@modelcontextprotocol/server-filesystem,./workspace}', false)
on conflict (id) do nothing;

-- ---------------- agentes ----------------
insert into agents (id, description, model_id, system_prompt, initial_tools, allowed_tools, can_delegate_to, max_steps, max_tokens) values
 ('orchestrator', 'Orquestrador: entende o objetivo, planeja, delega aos especialistas e consolida a entrega.', 'foundry_default',
$$Voce e o orquestrador de uma equipe de agentes autonomos. Trabalhe sem pedir confirmacao ao usuario.
1. Entenda o objetivo e quebre em subtarefas.
2. Use list_agents para conhecer a equipe e delegate_task para delegar (passe contexto completo, o especialista nao ve esta conversa).
3. Use create_task quando a subtarefa puder rodar em segundo plano.
4. Se precisar de uma capacidade que nao tem, use search_tools.
5. Ao final, responda com um resumo objetivo do que foi entregue e onde (arquivos, resultados).
Responda em portugues.$$,
  '{search_tools,list_agents,delegate_task,create_task,check_task,memory_save,memory_recall}', '{*}', '{*}', 20, 400000),

 ('researcher', 'Pesquisador: busca na base de conhecimento interna, na web e em APIs publicas; devolve fatos com fonte.', 'foundry_default',
$$Voce e um pesquisador. Encontre fatos usando suas ferramentas; se nao tiver a ferramenta certa, use search_tools para descobri-la.
Cite a fonte de cada fato (documento da base, URL ou API). Seja conciso. Responda em portugues.$$,
  '{search_tools,kb_search,web_search,http_get}', '{kb_*,web_search,http_get,weather_forecast,cep_lookup,memory_*,think}', '{}', 12, 150000),

 ('coder', 'Programador: escreve e executa scripts (Python/shell) no workspace e reporta a saida real.', 'foundry_default',
$$Voce e um programador. Escreva o codigo com write_file e execute com run_shell (ex.: python3 arquivo.py).
Se nao tiver a ferramenta necessaria, use search_tools. Reporte a saida real da execucao, nunca invente.
Responda em portugues.$$,
  '{search_tools,write_file}', '{read_file,write_file,list_files,run_shell,delete_file}', '{}', 12, 150000),

 ('writer', 'Redator: produz documentos markdown claros e salva no workspace.', 'foundry_default',
$$Voce e um redator tecnico. Produza textos claros e estruturados em markdown e salve com write_file quando pedido.
Use search_tools se precisar de outra capacidade. Responda em portugues.$$,
  '{search_tools,write_file}', '{read_file,write_file,list_files,kb_*}', '{}', 10, 120000),

 ('assistant', 'Assistente de chat: conversa com o usuario e resolve sozinho, pesquisando, planejando, usando ferramentas e delegando aos especialistas.', 'foundry_default',
$$Voce e o assistente principal da plataforma e conversa com o usuario em um chat. Trabalhe de forma autonoma: use as ferramentas sem pedir licenca (as que exigem aprovacao pedem sozinhas).
Como decidir o que fazer:
- Conversa ou pergunta simples que voce sabe com seguranca: responda direto.
- Fatos externos, atuais ou que podem ter mudado (versoes, precos, noticias, documentacao, comparacoes): pesquise com web_search, leia as melhores fontes com http_get e cite as URLs. Nunca responda de memoria nesses casos.
- Assuntos internos da empresa (politicas, arquitetura, operacao): kb_search.
- Tarefas com varios passos: registre o plano com update_plan e atualize o status de cada passo conforme avanca.
- Antes de decisoes dificeis ou depois de um resultado inesperado de ferramenta, use think.
- Trabalho especializado (escrever e executar codigo, documentos longos, pesquisa extensa): delegue com delegate_task passando todo o contexto, porque o especialista nao ve esta conversa. Use list_agents para conhecer a equipe. Trabalho demorado e independente: create_task.
- Precisa de uma capacidade que nao tem: search_tools.
- Fatos duraveis sobre o usuario, preferencias ou decisoes: memory_save.
- Pedido ambiguo antes de comecar: pergunte na propria resposta. Duvida no meio de uma tarefa (informacao que so a pessoa tem, ou escolha antes de um passo irreversivel): use ask_human e continue com a resposta.
- Se uma ferramenta for negada, leia o motivo, ajuste o plano e nao tente contornar.
Responda em portugues, em markdown, de forma objetiva. Ao terminar uma tarefa, diga o que foi feito e onde estao os resultados.$$,
  '{search_tools,think,update_plan,ask_human,web_search,http_get,kb_search,memory_save,memory_recall,list_agents,delegate_task,create_task,check_task}',
  '{*}', '{researcher,coder,writer}', 25, 400000)
on conflict (id) do update set description = excluded.description, model_id = excluded.model_id,
  system_prompt = excluded.system_prompt, initial_tools = excluded.initial_tools, allowed_tools = excluded.allowed_tools,
  can_delegate_to = excluded.can_delegate_to, max_steps = excluded.max_steps, max_tokens = excluded.max_tokens,
  version = agents.version + case when agents.system_prompt is distinct from excluded.system_prompt then 1 else 0 end,
  updated_at = now();

-- ---------------- base de conhecimento ----------------
delete from kb_documents where source = 'seed';
insert into kb_documents (title, content, source) values
 ('Politica de aprovacao de ferramentas',
  'Cada ferramenta tem uma politica: auto (executa sem aprovacao), approval (exige aprovacao humana ou do modo de aprovacao configurado) e deny (nunca executa). Ferramentas que executam codigo, enviam mensagens ou alteram dados externos devem comecar como approval. A decisao de cada aprovacao fica registrada no log de eventos do run.', 'seed'),
 ('Budget e limites de execucao',
  'Todo run tem limite de passos (max_steps) e de tokens (max_tokens). Um subagente recebe como budget o menor valor entre o seu proprio limite e o saldo do agente pai, e o consumo do filho e descontado do pai. Ao estourar o budget o run termina com status budget_exceeded. O custo em dolares e calculado pela tabela models.', 'seed'),
 ('Execucao duravel e retomada',
  'Workflows gravam um checkpoint por no em workflow_node_runs. Se o processo cair, o comando de retomada reexecuta apenas os nos que nao terminaram, reaproveitando as saidas ja gravadas. Runs de agentes registram cada chamada de modelo e de ferramenta em run_events.', 'seed'),
 ('Deploy on-premises',
  'O sistema roda como servico (Windows Service ou container) junto de um Postgres local. Modelos podem ser locais (Foundry Local, Ollama, vLLM) ou em nuvem (Azure Foundry, Anthropic), escolhidos por agente na tabela models. Credenciais ficam em variaveis de ambiente ou cofre, nunca no banco.', 'seed'),
 ('Descoberta de ferramentas',
  'Agentes comecam com poucas ferramentas. A ferramenta search_tools faz busca textual no registro e carrega as ferramentas encontradas, respeitando o escopo allowed_tools do agente. Ferramentas HTTP e MCP podem ser cadastradas no banco sem alterar codigo.', 'seed');

-- ---------------- workflows ----------------
insert into workflows (id, description, definition) values
 ('relatorio_tecnico', 'Pesquisa -> (resumo || riscos) em paralelo -> consolidacao em arquivo',
  '{
    "nodes": {
      "contexto":   {"type": "tool",  "tool": "kb_search", "args": {"query": "{{input}}", "limit": 3}, "next": ["pesquisar"]},
      "pesquisar":  {"type": "agent", "agent": "researcher", "input": "Tema: {{input}}\n\nTrechos da base interna:\n{{contexto}}\n\nLevante os fatos principais sobre o tema, com fonte.", "next": ["resumir", "riscos"]},
      "resumir":    {"type": "agent", "agent": "writer", "input": "Resuma em ate 5 bullets, sem salvar arquivo:\n{{pesquisar}}", "next": ["consolidar"]},
      "riscos":     {"type": "agent", "agent": "researcher", "input": "Com base nos fatos abaixo, liste 3 riscos operacionais e a mitigacao de cada um:\n{{pesquisar}}", "next": ["consolidar"]},
      "consolidar": {"type": "agent", "agent": "writer", "input": "Monte um relatorio markdown com titulo, secao Resumo e secao Riscos e salve em relatorio_workflow.md com write_file.\n\nResumo:\n{{resumir}}\n\nRiscos:\n{{riscos}}", "next": []}
    }
  }')
on conflict (id) do update set description = excluded.description, definition = excluded.definition,
  version = workflows.version + case when workflows.definition is distinct from excluded.definition then 1 else 0 end;
