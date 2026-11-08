-- Opcional, so para ambiente sem internet: aponta as ferramentas HTTP para o dublê local (tests/mock_llm.py).
-- Mostra tambem que mudar o comportamento de uma ferramenta e so um UPDATE no banco.
update tools set config = jsonb_set(config, '{url}', '"http://localhost:8765/fake/weather?latitude={latitude}&longitude={longitude}"')
 where name = 'weather_forecast';
update tools set config = jsonb_set(config, '{url}', '"http://localhost:8765/fake/cep/{cep}"')
 where name = 'cep_lookup';
