# Plano de Sprints — Gestão de Materiais

## Objetivo

Entregar primeiro um MVP utilizável pelo setor de TI e estruturar desde o início a separação lógica de dados por setor. Não habilitar usuários de outros setores até validar isolamento e permissões.

## Sprint 0 — Descoberta e decisões (2–3 dias)

- Confirmar versão alvo do .NET, mecanismo de login/identidade corporativa e arquitetura de hospedagem Azure.
- Definir o fluxo de publicação da VM: artefato de build/teste via GitHub Actions e implantação manual controlada antes de automatizar.
- Definir onde o build e os testes serão executados. O SDK pode ficar no CI/ambiente de build, não necessariamente no computador do desenvolvedor; o processo precisa publicar artefatos reproduzíveis.
- Banco escolhido: PostgreSQL 16 instalado na VM Ubuntu 24.04; definir política de backup e restauração antes do piloto.
- Confirmar perfis do piloto: administrador, gestor, assistente e auxiliar.
- Mapear fluxo de entrada, retirada/consumo, ajuste, devolução e eventual transferência.
- Examinar telas reais, CSS, tokens, componentes e imagens do sistema de brindes para registrar cores, tipografia, ícones, espaçamentos e comportamento responsivo.
- Definir se saída é consumo definitivo, empréstimo de patrimônio, ou ambos; para monitores e periféricos, validar necessidade de número de série e atribuição a pessoa.

Aceite: decisões documentadas; escopo do piloto aprovado; VM Linux + PostgreSQL escolhidos; build/testes automatizados definidos. A VM foi criada; o serviço PostgreSQL e o runtime ASP.NET Core foram instalados. O banco da aplicação, o deploy e o backup ainda estão pendentes.

## Sprint 1 — Fundação e identidade visual (1 sprint)

- Criar solução ASP.NET Core com camadas web, domínio e persistência, configuração por ambiente e documentação de execução.
- Montar layout base responsivo reproduzindo o design system observado no sistema de brindes: navegação lateral, cabeçalho, cards, formulários, tabela e estados de carregamento/erro/vazio.
- Implementar autenticação e autorização inicial; provisionar setor TI e usuários piloto.
- Configurar migrations e banco local de desenvolvimento.

Aceite: aplicação inicia localmente e na VM, login funcional, navegação consistente em desktop/tablet, migration PostgreSQL aplicada e nenhum segredo versionado. Status atual: solução inicial, login local, autorização por perfil, layout e migration PostgreSQL foram implementados; PostgreSQL 16 e runtime ASP.NET Core 10 estão ativos na VM. Falta criar banco/usuário da aplicação, configurar a conexão segura e publicar a aplicação.

## Sprint 2 — Catálogo e saldos por setor (1 sprint)

- CRUD de materiais e categorias, incluindo unidade, código, descrição, ativo/inativo e estoque mínimo.
- Modelar setor e estoque por setor; cadastrar o setor TI como piloto.
- Definir por material se o controle é por quantidade ou por unidade individual com série/patrimônio.
- Lista com busca, filtros, paginação e detalhe do material.
- Validar campos e impedir duplicidades relevantes (ex.: código dentro do setor ou catálogo global, conforme decisão).

Aceite: cadastrar, editar, consultar e desativar material; não excluir registros com movimentações; saldo inicial registrado por movimento de entrada.

## Sprint 3 — Movimentações e auditoria (1 sprint)

- Entrada, saída por consumo, empréstimo e devolução; ajuste justificado. Ajustes de assistentes/auxiliares aguardam aprovação de gestor antes de alterar o saldo.
- Atualizar saldo e criar movimento em transação atômica; bloquear saída acima do saldo disponível.
- Registrar usuário, setor, tipo, quantidade, justificativa/referência e horário.
- Histórico filtrável por período, material, tipo e usuário.

Aceite: saldo nunca é editado diretamente; movimento e saldo permanecem consistentes após falha; trilha de auditoria consultável.

## Sprint 4 — Painel, alertas e relatórios (1 sprint)

- Dashboard com itens cadastrados, saldo baixo e últimas movimentações.
- Alertas visuais para estoque abaixo do mínimo (notificação externa fica fora até decisão).
- Relatórios e exportação CSV, respeitando setor autorizado.
- Polimento responsivo e acessibilidade por teclado/leitor de tela.

Aceite: indicadores conferem com banco e filtros; usuário só recebe dados do setor autorizado.

## Sprint 5 — Segurança, homologação e piloto (1 sprint)

- Testes unitários, integração e autorização multi-setor; testar usuário tentando acessar outro setor por URL/API.
- Revisar validação, CSRF, sessão/cookies, logs sem dados sensíveis, backup e restauração.
- Testes de concorrência e idempotência para evitar saldo negativo ou lançamento duplicado.
- Homologação com usuários de TI, correção de achados e manual curto.

Aceite: suíte aprovada, isolamento comprovado, restauração testada e checklist de operação assinado.

## Backlog pós-piloto

- Aprovação de solicitações de material.

- Transferências entre setores com confirmação bilateral.
- Integração com diretório corporativo, notificações e ERP.
- Entrada em produção para novos setores após revisão de permissões, suporte e capacidade.

## Decisões registradas

Decisão registrada: o piloto usará contas locais do ASP.NET Core Identity, começando pelo usuário `adm`. Senhas nunca devem ser armazenadas em cookies, código-fonte ou documentação. O Identity armazena hashes de senha e a autenticação usa cookie protegido pelo ASP.NET Core Data Protection, com `HttpOnly`, `Secure` e `SameSite` configurados. O cadastro público fica desativado.

Provisionar a senha inicial por configuração segura fora do repositório; nunca embutir senha padrão no código. Credenciais que tenham sido expostas devem ser trocadas antes de uso real.

- Saída: consumo definitivo e empréstimo, com devolução para empréstimos.
- Rastreamento: suportar itens controlados por quantidade e ativos rastreados individualmente por número de série/patrimônio. A classificação será definida por material.
- Perfis: administrador pode executar todas as operações; gestor pode executar todas as operações; assistente e auxiliar podem operar, exceto ajustes. Ajustes iniciados por assistente/auxiliar exigem aprovação de gestor antes de alterar o saldo. Registrar solicitante, aprovador e justificativa na auditoria.
- Infraestrutura: Azure VM Ubuntu 24.04, tamanho Standard D2als v6, PostgreSQL 16 instalado localmente; banco limitado a localhost e sem porta 5432 pública.
- Hospedagem: aplicação ASP.NET Core e PostgreSQL na mesma VM. Região atual: East US. Domínio, TLS, política de backup e recuperação ainda precisam ser definidos.
- Build/deploy: GitHub Actions para build/testes e publicação de artefato; deploy na VM será controlado e verificado antes de habilitar automação. Não guardar chaves ou senhas no repositório.
- Importação: preparar importação inicial por CSV validado, com pré-visualização/relatório de erros e saldo inicial registrado como movimento auditável. Confirmar o arquivo real e o mapeamento antes de executar a carga.

Ainda é necessário configurar a base PostgreSQL da aplicação e seu usuário, preparar o serviço systemd/reverse proxy, definir domínio/TLS, backup e restauração, além de estabelecer permissões do pipeline. Nenhum segredo deve entrar no repositório.

Estimativas são indicativas; considerar duração como hipótese e ajustar após Sprint 0.
