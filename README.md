# Sistema de Gestão de Materiais

Aplicação web em C#/.NET para controle de materiais por setor, inspirada no sistema de Gestão de Brindes existente. O primeiro piloto atende o setor de TI; o modelo deverá suportar múltiplos setores com isolamento dos dados por setor.

## Diretrizes

- Stack: ASP.NET Core 10, Razor Pages, ASP.NET Core Identity e Entity Framework Core. SQLite é usado no desenvolvimento local; PostgreSQL 16 é o destino da VM Ubuntu 24.04.
- Autorização sempre aplicada no servidor: usuário do setor piloto não pode consultar nem alterar dados de outro setor.
- Lançamentos de estoque devem gerar movimentações auditáveis; não editar saldo diretamente.
- Interface responsiva, acessível e alinhada à identidade visual do sistema de brindes.
- O sistema legado é referência de produto/design, não dependência de runtime nem fonte de código a copiar sem necessidade.

## Escopo do piloto

Catálogo de materiais (mouse, teclado, headset, monitor e outros), categorias, busca e filtros por categoria/situação/tipo de controle, paginação e detalhes do material, saldo por setor, entradas, consumo, empréstimos e devoluções, ajustes justificados com aprovação para assistentes/auxiliares, cadastro de ativos individualizados por série/patrimônio com empréstimo, devolução e consumo unitário, consulta de movimentações, níveis mínimos e painel de estoque baixo. O piloto começa com um setor de TI, mas inclui `SetorId` nos dados e autorização para permitir expansão futura.

## Desenvolvimento e implantação

A aplicação será publicada em uma VM Azure Ubuntu 24.04. PostgreSQL 16 e o runtime ASP.NET Core 10 estão instalados na mesma VM; o PostgreSQL deve aceitar conexões apenas locais. O SDK .NET 10 é necessário no ambiente de build, não no servidor de produção.

O fluxo previsto é: desenvolver e versionar os arquivos → GitHub Actions executa testes padrão/integração PostgreSQL 16 e formatação → publica bundle da aplicação + script SQL idempotente com SHA-256 → implantação manual controlada na VM. A workflow local ainda precisa executar no GitHub. A equipe de TI informou ao responsável que já realizou a revisão e homologação; isso não substitui o gate operacional de backup/restauração. O inventário remoto confirmou PostgreSQL 16.15 e runtime ASP.NET Core 10.0.15 ativos, banco `sistema_materiais` existente com apenas a migration inicial do Identity, serviço da aplicação ainda não registrado/ativo e Caddy servindo conteúdo estático na rota raiz, além de rotas preexistentes de outros serviços que devem ser preservadas. O bundle legado encontrado na VM contém `app.db` e configuração Development e foi rejeitado para implantação. Foi criado backup pré-mudança root-only na VM e sua restauração em banco descartável PostgreSQL 16 passou; ainda falta copiar o backup para destino externo aprovado e definir retenção. O banco, a configuração ativa do Caddy e os serviços existentes devem ser preservados; faltam release aprovada pela CI, provisionamento mínimo da aplicação e rota de homologação. Nunca versionar senha ou chave SSH.

Siga `docs/SPRINTS.md`. A solução ASP.NET Core foi iniciada na Sprint 1.

## Estrutura

- `docs/SPRINTS.md` — plano incremental, critérios de aceite e decisões pendentes.
- `docs/MANUAL-PILOTO.md` — manual rápido em rascunho para revisão com a equipe de TI antes do piloto.
- `docs/IMPORTACAO-INICIAL.md` — contrato do modelo CSV, validação, prévia, confirmação e gravação atômica.
- `docs/PLANO-OPERACIONAL-VM.md` — rascunho de preparação; exige validação do responsável antes de qualquer alteração na VM.
- `docs/PROMPT-BASE.md` — prompt refinado para orientar a implementação por sprint.

## Executar localmente

Requisitos: .NET 10 SDK.

Na raiz do repositório (`Sistemamateriais`):

```sh
dotnet tool restore
dotnet restore SistemaMateriais.slnx
dotnet test SistemaMateriais.slnx --no-restore
dotnet format SistemaMateriais.slnx --verify-no-changes --no-restore
dotnet run --project src/SistemaMateriais.Web/SistemaMateriais.Web.csproj --launch-profile https -- '--Database:Provider=Sqlite' '--ConnectionStrings:DefaultConnection=Data Source=app.db;Cache=Shared'
```

O perfil `https` inicia a aplicação em `https://localhost:7167` e redireciona o endereço HTTP para HTTPS. Os argumentos fixam SQLite para o desenvolvimento local, mesmo quando configurações do usuário selecionam outro provedor. Se o navegador avisar que o certificado local não é confiável, confie no certificado de desenvolvimento do .NET antes de usar autenticação no navegador.

O script PostgreSQL idempotente em `deploy/postgresql-migrations.sql` contém as migrations locais preparadas e serializa execuções concorrentes com advisory lock; foi aplicado em bancos descartáveis PostgreSQL 17 e testado com referência histórica entre setores. Os testes opt-in exercitam fluxo HTTP de ativos, empréstimos/devoluções/consumo concorrentes, cadastro concorrente de identificadores e o preflight da migration; os testes SQLite também verificam que relações de catálogo e movimentos não cruzam setores. A VM foi identificada como PostgreSQL 16.15, mas as migrations ainda não foram aplicadas nela. A equipe de TI informou homologação concluída; permanecem necessários CI remoto e validação operacional de backup/restauração antes da mudança externa.

O ambiente Development cria o banco SQLite local e os quatro perfis na primeira inicialização. Para criar o administrador inicial, configure `BootstrapAdmin__Password` como variável de ambiente segura; o nome padrão é `adm` e pode ser alterado com `BootstrapAdmin__UserName`. A aplicação não contém senha padrão. A conta é criada apenas se ainda não existir.

Na VM, configurar `Database__Provider=PostgreSql` e `ConnectionStrings__PostgreSql` por arquivo de ambiente protegido ou mecanismo equivalente, fora do repositório. Configure `BootstrapAdmin__Password` apenas no provisionamento inicial do administrador e remova-a depois. O usuário PostgreSQL da aplicação deve ter acesso mínimo; não abrir a porta 5432 à internet.
