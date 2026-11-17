# Plano de Sprints — Gestão de Materiais

## Objetivo

Entregar primeiro um MVP utilizável pelo setor de TI e estruturar desde o início a separação lógica de dados por setor. Não habilitar usuários de outros setores até validar isolamento e permissões.

## Sprint 0 — Descoberta e decisões (2–3 dias)

- Confirmar versão alvo do .NET, mecanismo de login/identidade corporativa e arquitetura de hospedagem Azure.
- Definir o fluxo de publicação da VM: build/testes e artefato via GitHub Actions; primeiro deploy manual controlado antes de automatizar. A workflow de CI foi criada localmente; ainda precisa ser enviada ao GitHub e executada.
- Definir onde o build e os testes serão executados. O SDK pode ficar no CI/ambiente de build, não necessariamente no computador do desenvolvedor; o processo precisa publicar artefatos reproduzíveis.
- Banco escolhido: PostgreSQL 16 instalado na VM Ubuntu 24.04; definir política de backup e restauração antes do piloto.
- Confirmar perfis do piloto: administrador, gestor, assistente e auxiliar.
- Mapear fluxo de entrada, retirada/consumo, ajuste, devolução e eventual transferência.
- Examinar telas reais, CSS, tokens, componentes e imagens do sistema de brindes para registrar cores, tipografia, ícones, espaçamentos e comportamento responsivo.
- Definir se saída é consumo definitivo, empréstimo de patrimônio, ou ambos; para monitores e periféricos, validar necessidade de número de série e atribuição a pessoa.

Aceite: decisões documentadas; escopo do piloto aprovado; VM Linux + PostgreSQL escolhidos; build/testes automatizados definidos. A versão .NET 10 SDK 10.0.403 está fixada em `global.json`; `.github/workflows/ci.yml` define testes padrão e PostgreSQL 16, migrations, formatação e publicação de bundle da aplicação + SQL de migrations com checksum SHA-256 usando `actions/upload-artifact@v7`. O pacote foi montado e verificado localmente, sem banco/segredos; a workflow ainda não foi executada no GitHub. A VM existe e PostgreSQL/runtime ASP.NET Core estão instalados, mas o banco da aplicação, deploy e backup/restauração continuam pendentes. `docs/PLANO-OPERACIONAL-VM.md` permanece NÃO VALIDADO: não há aprovação formal registrada nem destino/retenção/teste de restauração preenchidos.

## Sprint 1 — Fundação e identidade visual (1 sprint)

- Criar solução ASP.NET Core com camadas web, domínio e persistência, configuração por ambiente e documentação de execução.
- Montar layout base responsivo reproduzindo o design system observado no sistema de brindes: navegação lateral, cabeçalho, cards, formulários, tabela e estados de carregamento/erro/vazio.
- Implementar autenticação e autorização inicial; provisionar setor TI e usuários piloto.
- Configurar migrations e banco local de desenvolvimento.

Aceite: aplicação inicia localmente e na VM, login funcional, navegação consistente em desktop/tablet, migration PostgreSQL aplicada e nenhum segredo versionado. Status local: solução ASP.NET Core 10, login Identity local, autorização por perfil, layout e migration inicial implementados. Um smoke HTTPS isolado com SQLite descartável confirmou redirecionamento para login, POST com antiforgery, autenticação e renderização do painel; o cookie de autenticação foi verificado como Secure/HttpOnly/SameSite=Lax. A folha de estilos local compartilha os tokens principais (azul hue 221, sidebar 260px) e padrões de menu desktop/offcanvas da referência disponível `D:/Projetos/GestaoBrindesPHP/docs/UI-UX-Design.md` e `public/assets/css/app.css`. A página de login foi inspecionada no navegador em 1264×569, 768×1024 e 390×844; não houve overflow horizontal em 390px. Os campos usuário/senha/checkbox estão associados a rótulos acessíveis e a navegação por Tab percorre usuário, senha, manter conectado e entrar. A validação manual não cobre as páginas autenticadas nem todos os fluxos. O painel foi verificado por HTTP após autenticação local, mas a navegação autenticada não foi inspecionada visualmente em todos os breakpoints; essa homologação manual permanece pendente. Na VM, evidência anterior registra PostgreSQL 16 e runtime ASP.NET Core instalados, porém a aplicação não está ativa: Caddy serve conteúdo estático em `/usr/share/caddy` e não há unidade systemd da aplicação. Ainda faltam banco/usuário, publicação, migration PostgreSQL 16 e validação do login na VM; nenhum desses passos foi executado nesta rodada.

## Sprint 2 — Catálogo e saldos por setor (1 sprint)

- CRUD de materiais e categorias, incluindo unidade, código, descrição, ativo/inativo e estoque mínimo.
- Modelar setor e estoque por setor; cadastrar o setor TI como piloto.
- Definir por material se o controle é por quantidade ou por unidade individual com série/patrimônio.
- Lista com busca, filtros, paginação e detalhe do material.
- Validar campos e impedir duplicidades relevantes (ex.: código dentro do setor ou catálogo global, conforme decisão).

Aceite: cadastrar, editar, consultar e desativar material; não excluir registros com movimentações; saldo inicial registrado por movimento de entrada.

Status parcial (local, 16/11/2026): o catálogo de materiais e categorias suporta CRUD, setor TI piloto, código, unidade, descrição, mínimo e situação ativa/inativa. A lista de materiais tem busca textual, filtros por categoria, situação e tipo de controle, paginação com filtros preservados e página de detalhes com saldo calculado pelo livro de movimentações; testes HTTP cobrem os filtros, paginação, saldo exibido e isolamento por setor. Foram adicionados `global.json` e `.github/workflows/ci.yml`; a workflow ainda não foi executada no GitHub. Continua pendente a confirmação formal da regra de unicidade do código (a implementação atual aplica unicidade por setor), além da validação manual responsiva/acessível e da aplicação externa das migrations. A edição agora impede trocar o tipo de controle após movimentos ou ativos vinculados; um teste HTTP confirma que, havendo entrada registrada, a troca é recusada e o tipo e o histórico permanecem intactos. A troca continua permitida antes do primeiro registro. A suíte padrão usa SQLite; testes opt-in cobrem apenas fluxos específicos no PostgreSQL 17 descartável. PostgreSQL 16/VM e publicação continuam pendentes. Não alterar a VM sem plano validado de banco, serviço e backup.

## Sprint 3 — Movimentações e auditoria (1 sprint)

- Entrada, saída por consumo, empréstimo e devolução; ajuste justificado. Ajustes de assistentes/auxiliares aguardam aprovação de gestor antes de alterar o saldo.
- Atualizar saldo e criar movimento em transação atômica; bloquear saída acima do saldo disponível.
- Registrar usuário, setor, tipo, quantidade, justificativa/referência e horário.
- Histórico filtrável por período, material, tipo e usuário.

Aceite: saldo nunca é editado diretamente; movimento e saldo permanecem consistentes após falha; trilha de auditoria consultável.

Status parcial (local, 17/11/2026): telas autenticadas para entrada e consumo no setor TI; entrada grava quantidade positiva e consumo verifica saldo em transação serializável, bloqueando retirada superior ao saldo. Movimentos registram material/setor, tipo, quantidade, referência, usuário e horário UTC. O histórico tem filtros por material, tipo, período e usuário e mostra até 500 lançamentos recentes. Empréstimos por quantidade e devoluções parciais/totais foram implementados com responsável, previsão/referência e vínculo auditável; o saldo em aberto deriva dos movimentos de empréstimo/devolução. Ativos individualizados são registrados por série/patrimônio, com identificador normalizado e único por setor; cada entrada, empréstimo, devolução e consumo unitário atualiza o estado e registra movimento vinculado ao ativo, com validação de que formulários quantitativos não alterem ativos individuais. Testes HTTP locais cobrem cadastro, bloqueio de entrada/consumo por quantidade, empréstimo unitário, devolução e consumo auditável. Ajustes têm diferença assinada e justificativa: gestor/administrador aplica diretamente; assistente/auxiliar cria pedido pendente, que gestor/administrador pode aprovar ou rejeitar. Aprovação registra o decisor e cria movimento atomicamente; autoaprovação é negada; operações não deixam saldo negativo. Testes HTTP cobrem empréstimo, devolução parcial/total/excedente, ajuste imediato, pedido/aprovação/rejeição, saldo insuficiente, roles permitidos/negados e isolamento de setor no fluxo. Testes concorrentes em PostgreSQL 17 descartável cobrem ajustes gerenciais, aprovações e rejeições simultâneas, empréstimos quantitativos, devoluções quantitativas e operações concorrentes de ativos. Idempotência está implementada para entrada, consumo, empréstimos/devoluções quantitativas, ajustes e ativos individualizados (cadastro, empréstimo, devolução e consumo), com chave UUID e fingerprint SHA-256: replay idêntico não duplica; reuso da chave com payload divergente retorna HTTP 409. Testes HTTP em SQLite cobrem replay e payload divergente em cada ação de ativo; PostgreSQL 17 confirma replay concorrente de cadastro de ativo e replays persistidos de cadastro/empréstimo/devolução/consumo. Replays concorrentes PostgreSQL de entrada, consumo, empréstimo quantitativo, devolução parcial e ajuste também passaram. Essa cobertura é restrita e não valida PostgreSQL 16 da VM, aplicação externa das migrations, revisão manual das telas autenticadas/acessibilidade nem operação da VM.

## Sprint 4 — Painel, alertas e relatórios (1 sprint)

- Dashboard com itens cadastrados, saldo baixo e últimas movimentações.
- Alertas visuais para estoque abaixo do mínimo (notificação externa fica fora até decisão).
- Relatórios e exportação CSV, respeitando setor autorizado.
- Polimento responsivo e acessibilidade por teclado/leitor de tela.

Aceite: indicadores conferem com banco e filtros; usuário só recebe dados do setor autorizado.

Status parcial (local, 16/11/2026): painel com contagem de materiais ativos, itens abaixo do mínimo, lista de reposição e cinco movimentos recentes do setor TI; também mostra a contagem de empréstimos ainda abertos, calculada pelas devoluções registradas. O alerta visual acessível (`role=status`, anúncio polite) mostra a contagem e link para reposição e é omitido quando não há falta; testes HTTP cobrem ambos os estados. Dois relatórios CSV: histórico de movimentos (filtros por material, tipo, período e usuário) e lista de reposição (ativos abaixo do mínimo), limitados ao setor TI, BOM UTF-8, escaping e neutralização de fórmulas; testes verificam conteúdo, filtros, link e segurança. A página de login foi inspecionada em 1264×569, 768×1024 e 390×844; não apresentou overflow horizontal em 390px, os campos estão associados a rótulos e a ordem de Tab foi conferida. Revisão visual/acessível das páginas autenticadas permanece pendente. Alertas externos e relatórios adicionais continuam sem escopo aprovado.

## Sprint 5 — Segurança, homologação e piloto (1 sprint)

- Testes unitários, integração e autorização multi-setor; testar usuário tentando acessar outro setor por URL/API.
- Revisar validação, CSRF, sessão/cookies, logs sem dados sensíveis, backup e restauração.
- Testes de concorrência e idempotência para evitar saldo negativo ou lançamento duplicado.
- Homologação com usuários de TI, correção de achados e manual curto.

Aceite: suíte aprovada, isolamento comprovado, restauração testada e checklist de operação assinado.

Status parcial (local, 17/11/2026): testes HTTP cobrem autorização por perfil e isolamento do setor TI em empréstimos, devoluções, ajustes e decisões de pedidos; testes adicionais cobrem cadastro, transições unitárias e bloqueio de acesso a ativo de outro setor. Um teste HTTP confirma que criar categoria sem token antiforgery retorna 400 e não persiste dados. `dotnet list SistemaMateriais.slnx package --vulnerable --include-transitive` não encontrou pacotes vulneráveis nos projetos web/testes, conforme os feeds NuGet consultados. A equipe de TI informou que já realizou revisão e homologação; `docs/MANUAL-PILOTO.md` registra essa confirmação. A suíte padrão terminou com 98 aprovados e 11 testes PostgreSQL opt-in ignorados. A suíte opt-in PostgreSQL 17 terminou com 12 aprovados após aplicar migrations em cluster descartável. Foi aplicado o SQL de deploy em banco descartável e repetido sequencialmente; verificadas 11 migrations e os índices de idempotência. Foi testado também o preflight do SQL final em PostgreSQL 17 descartável parcialmente migrado: foi inserido um movimento histórico entre setores antes da migration de integridade; a execução foi interrompida pela mensagem diagnóstica prevista e a migration não ficou registrada (rollback). `dotnet build`, `dotnet format --verify-no-changes`, auditoria NuGet e `git diff --check`/`git diff --cached --check` passaram. A validação de idempotência de ativos individualizados está descrita na Sprint 3. Inventário remoto somente leitura confirmou PostgreSQL 16.15/runtime ASP.NET Core 10.0.15, mas a aplicação está inativa: banco `sistema_materiais` já existe com schema/migration inicial de Identity, sem leitura de registros; a role de runtime e o serviço systemd não existem. O pacote encontrado na VM inclui banco SQLite local e configuração Development e foi descartado. O SQL foi repetido duas vezes em cópia descartável PostgreSQL 16.15 derivada do backup e confirmou 11 migrations/índices; backup root-only modo 0600 restaurado em banco descartável, que foi removido. O backup ainda está apenas na mesma VM e não substitui cópia externa. Nenhuma migration ou mudança de proxy foi aplicada ao banco original/serviço. Continuam pendentes: workflow remoto GitHub, inspeção visual/a11y manual das telas autenticadas, cópia externa e retenção do backup, release aprovada pela CI, provisionamento da aplicação e deploy controlado. A subscription da VM não está autenticada no Azure CLI local.

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

O banco PostgreSQL `sistema_materiais` já existe com a migration inicial do Identity; não foi verificado nem copiado conteúdo de usuários. Ainda é necessário provisionar/validar a role mínima da aplicação, aplicar migrations após backup externo testado, instalar/ativar o serviço systemd e adicionar a rota Caddy preservando os serviços existentes. A homologação funcional da TI foi informada pelo usuário; CI, backup/restauração e gates operacionais permanecem pendentes. Nenhum segredo deve entrar no repositório.

Estimativas são indicativas; considerar duração como hipótese e ajustar após Sprint 0.
