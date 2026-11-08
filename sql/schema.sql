-- ============================================================
-- AgentPoc - schema (Postgres 14+). Idempotente.
-- Tudo que define comportamento de agente vive aqui.
-- ============================================================

create table if not exists providers (
    id              text primary key,
    kind            text not null check (kind in ('openai_compat', 'anthropic')),
    base_url        text not null,                 -- aceita ${ENV_VAR}
    api_key_env     text,                          -- nome da variavel de ambiente com a chave (nunca a chave)
    auth_header     text not null default 'bearer' check (auth_header in ('bearer', 'api-key', 'x-api-key', 'none')),
    extra_headers   jsonb not null default '{}'::jsonb,
    enabled         boolean not null default true
);

create table if not exists models (
    id                  text primary key,
    provider_id         text not null references providers(id),
    model_name          text not null,             -- aceita ${ENV_VAR} (ex.: nome do deployment no Foundry)
    supports_tools      boolean not null default true,
    supports_temperature boolean not null default false,
    max_tokens_param    text not null default 'max_completion_tokens' check (max_tokens_param in ('max_tokens', 'max_completion_tokens')),
    max_output_tokens   int not null default 4096,
    context_window      int not null default 128000,
    cost_in_per_mtok    numeric(12,4) not null default 0,
    cost_out_per_mtok   numeric(12,4) not null default 0,
    supports_streaming  boolean not null default true,
    reasoning_effort    text check (reasoning_effort in ('minimal', 'low', 'medium', 'high'))  -- so modelos de raciocinio (openai_compat)
);
alter table models add column if not exists supports_streaming boolean not null default true;
alter table models add column if not exists reasoning_effort text check (reasoning_effort in ('minimal', 'low', 'medium', 'high'));

create table if not exists agents (
    id               text primary key,
    description      text not null,
    model_id         text not null references models(id),
    system_prompt    text not null,
    initial_tools    text[] not null default '{}',     -- ferramentas carregadas no inicio
    allowed_tools    text[] not null default '{*}',    -- escopo de discovery (glob: '*', 'memory_*')
    can_delegate_to  text[] not null default '{}',     -- agentes que pode acionar ('*' = todos)
    max_steps        int not null default 15,
    max_tokens       int not null default 200000,      -- budget de tokens por run (in + out)
    temperature      real,
    version          int not null default 1,
    enabled          boolean not null default true,
    updated_at       timestamptz not null default now()
);

create table if not exists mcp_servers (
    id        text primary key,
    command   text not null,
    args      text[] not null default '{}',
    env       jsonb not null default '{}'::jsonb,
    enabled   boolean not null default false
);

create table if not exists tools (
    name        text primary key,
    description text not null,
    kind        text not null check (kind in ('builtin', 'http', 'mcp')),
    parameters  jsonb not null default '{"type":"object","properties":{}}'::jsonb,
    policy      text not null default 'auto' check (policy in ('auto', 'approval', 'deny')),
    config      jsonb not null default '{}'::jsonb,   -- http: {method,url,headers}; mcp: {server,tool}
    keywords    text not null default '',
    enabled     boolean not null default true,
    search      tsvector generated always as (to_tsvector('simple', name || ' ' || description || ' ' || keywords)) stored
);
create index if not exists ix_tools_search on tools using gin (search);

-- ---------------- execucao ----------------

create table if not exists runs (
    id             uuid primary key default gen_random_uuid(),
    parent_run_id  uuid references runs(id),
    root_run_id    uuid,
    workflow_run_id uuid,
    agent_id       text not null,
    agent_version  int not null default 1,
    model_id       text,
    input          text not null,
    output         text,
    conversation_id uuid,
    status         text not null default 'running'
                   check (status in ('running', 'completed', 'failed', 'budget_exceeded', 'max_steps', 'cancelled', 'waiting_human')),
    depth          int not null default 0,
    steps          int not null default 0,
    tokens_in      bigint not null default 0,
    tokens_out     bigint not null default 0,
    cost_usd       numeric(14,6) not null default 0,
    started_at     timestamptz not null default now(),
    finished_at    timestamptz
);
alter table runs add column if not exists conversation_id uuid;
alter table runs drop constraint if exists runs_status_check;
alter table runs add constraint runs_status_check
    check (status in ('running', 'completed', 'failed', 'budget_exceeded', 'max_steps', 'cancelled', 'waiting_human'));
create index if not exists ix_runs_root on runs(root_run_id);
create index if not exists ix_runs_conversation on runs(conversation_id) where conversation_id is not null;
create index if not exists ix_runs_parent on runs(parent_run_id);

create table if not exists run_events (
    id          bigserial primary key,
    run_id      uuid not null references runs(id),
    seq         int not null,
    type        text not null,
    data        jsonb not null default '{}'::jsonb,
    created_at  timestamptz not null default clock_timestamp()
);
create index if not exists ix_run_events_run on run_events(run_id, seq);

-- fila de tarefas assincronas (delegacao estilo ticket)
create table if not exists tasks (
    id              uuid primary key default gen_random_uuid(),
    agent_id        text not null references agents(id),
    input           text not null,
    status          text not null default 'queued' check (status in ('queued', 'running', 'done', 'failed')),
    priority        int not null default 0,
    created_by_run  uuid references runs(id),
    result_run_id   uuid references runs(id),
    attempts        int not null default 0,
    max_attempts    int not null default 3,
    locked_by       text,
    locked_at       timestamptz,
    heartbeat_at    timestamptz,
    result          text,
    error           text,
    notified_at     timestamptz,                       -- quando o resultado foi entregue a conversa que criou a tarefa
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now()
);
alter table tasks add column if not exists notified_at timestamptz;
create index if not exists ix_tasks_queue on tasks(priority desc, created_at) where status = 'queued';

-- memoria de longo prazo por agente
create table if not exists memories (
    id          bigserial primary key,
    agent_id    text not null,
    content     text not null,
    run_id      uuid,
    created_at  timestamptz not null default now(),
    search      tsvector generated always as (to_tsvector('simple', content)) stored
);
create index if not exists ix_memories_search on memories using gin (search);

-- base de conhecimento (RAG lexical; troque por pgvector em producao)
create table if not exists kb_documents (
    id          bigserial primary key,
    title       text not null,
    content     text not null,
    source      text not null default 'seed',
    created_at  timestamptz not null default now(),
    search      tsvector generated always as (to_tsvector('simple', title || ' ' || content)) stored
);
create index if not exists ix_kb_search on kb_documents using gin (search);

-- ---------------- workflows ----------------

create table if not exists workflows (
    id          text primary key,
    description text not null,
    definition  jsonb not null,
    version     int not null default 1,
    enabled     boolean not null default true
);

create table if not exists workflow_runs (
    id           uuid primary key default gen_random_uuid(),
    workflow_id  text not null references workflows(id),
    workflow_version int not null default 1,
    input        text not null,
    status       text not null default 'running' check (status in ('running', 'completed', 'failed', 'crashed')),
    output       text,
    started_at   timestamptz not null default now(),
    finished_at  timestamptz
);

create table if not exists workflow_node_runs (
    workflow_run_id uuid not null references workflow_runs(id),
    node_id         text not null,
    status          text not null check (status in ('running', 'completed', 'failed')),
    output          text,
    agent_run_id    uuid references runs(id),
    attempt         int not null default 1,
    started_at      timestamptz not null default now(),
    finished_at     timestamptz,
    primary key (workflow_run_id, node_id)
);

-- ---------------- chat ----------------

create table if not exists conversations (
    id                uuid primary key default gen_random_uuid(),
    agent_id          text not null references agents(id),
    title             text,
    active_tools      text[] not null default '{}',   -- ferramentas descobertas continuam carregadas entre turnos
    summary           text,                           -- resumo dos turnos antigos (compactacao)
    summarized_upto   int not null default 0,         -- mensagens com seq <= este valor estao no resumo
    created_at        timestamptz not null default now(),
    updated_at        timestamptz not null default now()
);

create table if not exists conversation_messages (
    id               bigserial primary key,
    conversation_id  uuid not null references conversations(id) on delete cascade,
    seq              int not null,
    role             text not null check (role in ('user', 'assistant', 'tool')),
    content          text,
    tool_calls       jsonb,
    tool_call_id     text,
    tool_name        text,
    run_id           uuid references runs(id),
    created_at       timestamptz not null default clock_timestamp(),
    unique (conversation_id, seq)
);

-- human-in-the-loop: aprovacoes de ferramentas e perguntas do agente (ask_human) aguardando uma pessoa
create table if not exists approvals (
    id               uuid primary key default gen_random_uuid(),
    run_id           uuid not null references runs(id),
    conversation_id  uuid references conversations(id) on delete cascade,
    kind             text not null default 'approval' check (kind in ('approval', 'question')),
    tool_call_id     text,                 -- liga a decisao a tool call; a retomada encontra a decisao por ela
    tool             text not null,
    args             jsonb not null default '{}'::jsonb,
    policy_reason    text,                 -- por que a aprovacao foi exigida (politica ou regra por argumento)
    question         text,
    options          jsonb,
    status           text not null default 'pending',
    reason           text,                 -- motivo da decisao (ex.: motivo da negacao)
    edited_args      jsonb,                -- argumentos corrigidos pelo humano antes de aprovar
    answer           text,                 -- resposta a uma pergunta
    created_at       timestamptz not null default now(),
    decided_at       timestamptz
);
alter table approvals add column if not exists kind text not null default 'approval';
alter table approvals add column if not exists tool_call_id text;
alter table approvals add column if not exists policy_reason text;
alter table approvals add column if not exists question text;
alter table approvals add column if not exists options jsonb;
alter table approvals add column if not exists edited_args jsonb;
alter table approvals add column if not exists answer text;
alter table approvals drop constraint if exists approvals_status_check;
alter table approvals add constraint approvals_status_check
    check (status in ('pending', 'approved', 'denied', 'answered', 'expired'));
create index if not exists ix_approvals_pending on approvals(conversation_id) where status = 'pending';
create index if not exists ix_approvals_call on approvals(run_id, tool_call_id);
