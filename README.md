# Sistema de Gestão de Materiais

Aplicação web em C#/.NET para controle de materiais por setor, inspirada no sistema de Gestão de Brindes existente. O primeiro piloto atende o setor de TI; o modelo deverá suportar múltiplos setores com isolamento dos dados por setor.

## Diretrizes

- Stack: ASP.NET Core 10, Razor Pages, ASP.NET Core Identity e Entity Framework Core. SQLite é usado no desenvolvimento local; PostgreSQL 16 é o destino da VM Ubuntu 24.04.
- Autorização sempre aplicada no servidor: usuário do setor piloto não pode consultar nem alterar dados de outro setor.
- Lançamentos de estoque devem gerar movimentações auditáveis; não editar saldo diretamente.
- Interface responsiva, acessível e alinhada à identidade visual do sistema de brindes.
- O sistema legado é referência de produto/design, não dependência de runtime nem fonte de código a copiar sem necessidade.

## Escopo do piloto

Catálogo de materiais (mouse, teclado, headset, monitor e outros), categorias, saldo por setor, entradas, saídas, ajustes justificados, consulta de movimentações, níveis mínimos e painel de estoque baixo. O piloto começa com um setor de TI, mas inclui `SetorId` nos dados e autorização para permitir expansão futura.

## Desenvolvimento e implantação

A aplicação será publicada em uma VM Azure Ubuntu 24.04. PostgreSQL 16 e o runtime ASP.NET Core 10 estão instalados na mesma VM; o PostgreSQL deve aceitar conexões apenas locais. O SDK .NET 10 é necessário no ambiente de build, não no servidor de produção.

O fluxo previsto é: desenvolver e versionar os arquivos → GitHub Actions executa build/testes → publicar artefato → implantação controlada na VM. A migration inicial do PostgreSQL foi gerada. Banco/usuário da aplicação, serviço systemd, domínio/TLS e rotina de backup ainda precisam ser configurados. Nunca versionar a senha do banco ou a chave SSH.

Siga `docs/SPRINTS.md`. A solução ASP.NET Core foi iniciada na Sprint 1.

## Estrutura

- `docs/SPRINTS.md` — plano incremental, critérios de aceite e decisões pendentes.
- `docs/PROMPT-BASE.md` — prompt refinado para orientar a implementação por sprint.

## Executar localmente

Requisitos: .NET 10 SDK.

Na pasta `NovoSistemaMateriais`:

```sh
dotnet tool restore
dotnet restore SistemaMateriais.slnx
dotnet test SistemaMateriais.slnx
dotnet run --project src/SistemaMateriais.Web
```

O ambiente Development cria o banco SQLite local e os quatro perfis na primeira inicialização. Para criar o administrador inicial, configure `BootstrapAdmin__Password` como variável de ambiente segura; o nome padrão é `adm` e pode ser alterado com `BootstrapAdmin__UserName`. A aplicação não contém senha padrão. A conta é criada apenas se ainda não existir.

Na VM, configurar `Database__Provider=PostgreSql` e `ConnectionStrings__PostgreSql` por arquivo de ambiente protegido ou mecanismo equivalente, fora do repositório. Configure `BootstrapAdmin__Password` apenas no provisionamento inicial do administrador e remova-a depois. O usuário PostgreSQL da aplicação deve ter acesso mínimo; não abrir a porta 5432 à internet.
