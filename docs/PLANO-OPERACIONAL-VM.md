# Plano operacional da VM — rascunho para validação

Status: revisão/homologação funcional da equipe de TI informada pelo usuário em 17/11/2026; implantação operacional ainda NÃO VALIDADA. O usuário autorizou prosseguir com as atividades necessárias, condicionadas à preservação dos serviços/dados existentes e aos controles de backup e rollback abaixo.

## Escopo e base conhecida

O inventário somente leitura, realizado após confirmação da identidade SSH pelo usuário, confirmou a VM do SistemaMateriais em Ubuntu 24.04, PostgreSQL 16.15 e runtime ASP.NET Core 10.0.15 ativos. Caddy atende o hostname e contém rotas preexistentes de outros serviços, que devem ser preservadas; a raiz ainda aponta para `/usr/share/caddy`. O serviço `sistema-materiais` não está registrado/ativo e `/opt/sistema-materiais` não existe, embora haja um arquivo de unidade em `/home/azureuser/sistema-materiais.service`. O banco `sistema_materiais` já existe, tem cerca de 7.9 MB e contém somente tabelas de Identity e `20261113204123_InitialIdentityPostgreSql`; não foram consultadas linhas de usuários. A role `sistema_materiais_app` e os arquivos de ambiente configurados no arquivo de unidade não existem. Um bundle antigo no diretório pessoal do operador contém `app.db` e `appsettings.Development.json` e não será usado. A subscription local autenticada não é a subscription da VM; o Azure CLI local não tem acesso à subscription correta.

O script `deploy/postgresql-migrations.sql` é local, idempotente e serializa execuções com advisory lock. Foi testado duas vezes sequencialmente em PostgreSQL 16.15 da VM, contra uma cópia descartável restaurada do banco antes da migração: 11 migrations e os dois índices de idempotência foram verificados; a base original não foi alterada pelo teste. Foi criado um dump pré-mudança root-only, modo 0600, e a restauração em outra base descartável foi verificada; os bancos e arquivos temporários de teste foram removidos. O backup permanece apenas na mesma VM, portanto ainda não é uma cópia externa/falha-independente e sua retenção não foi definida. Os fluxos opt-in exercitam HTTP, concorrência e preflight em ambiente descartável. Nenhuma migration foi aplicada no banco original e nenhuma rota/serviço foi alterado.

A proposta cobre somente schema e aplicação. Não inclui importação de dados, exposição pública do PostgreSQL, alteração de permissões de outros sistemas nem publicação automática. Se já houver dados ou serviços ativos não descritos, interromper e revisar o plano.

## Portões obrigatórios antes de qualquer alteração

1. O responsável valida por escrito janela de manutenção, domínio/TLS, nome de banco/role, responsável técnico e plano de retorno.
2. O responsável escolhe destino de backup protegido fora da VM, retenção, frequência e responsáveis pela restauração. Não prosseguir com backup somente no disco que será alterado.
3. Fazer inventário somente leitura: sistema/CPU/disco, serviços e portas, versão/configuração do Caddy e PostgreSQL, existência de bancos/roles, uso de disco, releases e backups existentes. Não revelar senhas, tokens ou conteúdo de arquivos de ambiente.
4. Produzir backup PostgreSQL consistente antes da mudança, incluindo objetos globais necessários; copiar para o destino aprovado, verificar integridade e testar restauração em banco isolado. Se a restauração não for comprovada, parar.
5. Confirmar que há espaço para backup, release atual e release nova; confirmar acesso administrativo de retorno e estado do DNS/certificado.

## Sequência proposta após validação

1. Criar role e banco exclusivos para a aplicação, com privilégio mínimo e conexão local; manter a porta 5432 inacessível pela rede pública.
2. Gerar e verificar o artefato de release a partir do commit aprovado; registrar hash, versão e resultado dos testes. Não construir a partir de arquivos locais não versionados.
3. Instalar a release em diretório versionado, criar usuário de serviço sem privilégios administrativos e configurar o serviço systemd para reiniciar com limites apropriados.
4. Armazenar configuração/segredos em arquivo protegido ou secret manager aprovado, fora do repositório e dos logs; permissões restritas ao serviço/administrador. Nunca colocar credenciais na linha de comando ou no histórico do shell.
5. Com backup pré-mudança confirmado, revisar o SQL idempotente e aplicá-lo em uma única sessão `psql -v ON_ERROR_STOP=1`, registrando os IDs aplicados; ao encerrar a conexão em caso de erro, o advisory lock de sessão é liberado. A migration de integridade setorial faz preflight das referências históricas e aborta com rollback se houver divergência; não corrige dados automaticamente. Não migrar dados de negócio sem plano próprio de origem, mapeamento, reconciliação e autorização.
6. Configurar Caddy como reverse proxy para o serviço local somente após validar domínio, TLS, headers e política de exposição; preservar uma cópia da configuração anterior.
7. Iniciar a aplicação e verificar health/readiness, login, acesso autorizado e negado, criação de movimento, logs sem segredo e conectividade exclusivamente local ao PostgreSQL.
8. Fazer backup pós-migração e testar restauração em banco isolado. Registrar versão, horário, operador, IDs de migration, resultados e incidentes.

## Retorno/rollback

- Antes da migration: parar a mudança e manter a release/configuração anterior.
- Após a migration: não presumir que downgrade de schema é seguro. Se a release anterior não for compatível, restaurar o backup pré-mudança em banco isolado, validar a restauração e só então executar o retorno aprovado; preservar evidências e não sobrescrever o banco original antes da decisão do responsável.
- Manter a release anterior e a cópia da configuração do proxy até a aceitação pós-mudança.

## Decisões que o responsável precisa preencher

- Domínio e responsável pelo DNS/TLS: pendente.
- Nome do banco/role e método de provisionamento de segredos: pendente.
- Destino protegido, frequência e retenção de backups: pendente.
- Evidência/critério de aprovação de restauração: pendente.
- Janela, operador e aprovador da mudança: pendente.
- Caminho e critério de rollback após migration: pendente.
- Confirmação de que não há dados/serviços preexistentes a preservar ou migrar: pendente.

## Critério para autorizar execução

O usuário autorizou as mudanças do projeto e informou que a homologação funcional da TI ocorreu. Foi criada somente uma cópia pré-mudança root-only do banco na VM e sua restauração foi testada; o banco original, o Caddy e os serviços não foram alterados. Ainda não iniciar migration, configuração Caddy, criação/ativação de serviço ou provisionamento de dados até copiar o backup para destino protegido fora da VM, aprovar a restauração, definir retenção e obter artefato de release aprovado pela CI. A subscription correta não está autenticada no Azure CLI local.