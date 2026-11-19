# Modelo de importação inicial — Sistema de Materiais

O CSV disponível para download contém somente o cabeçalho; não há dados fictícios nem arquivo de origem. Use **Importação inicial** no menu autenticado para baixar `modelo-importacao-materiais.csv`. A página, prévia e gravação são restritas a Administrador e Gestor.

## Contrato do arquivo

- Codificação UTF-8 com BOM; separador `;`; cabeçalho exato, na ordem apresentada.
- Uma linha por material, setor fixo TI.
- Decimais com vírgula e até três casas. Não use separador de milhar.
- Campos textuais são aparados nas extremidades conforme o campo; não há criação automática de categorias nem correção de cadastros existentes.

| Coluna | Obrigatória | Destino/regra |
|---|---|---|
| `Codigo` | Sim | Material.Code (1–40; aparar espaços externos) e NormalizedCode em maiúsculas invariantes; único dentro do setor TI. |
| `Nome` | Sim | Material.Name (2–120; aparar espaços externos). |
| `Descricao` | Não | Material.Description (até 500; aparar espaços externos); vazio equivale a nulo. |
| `Categoria` | Sim | Categoria ativa já existente no setor TI; aparar espaços externos e comparar pelo nome normalizado em maiúsculas invariantes. Não criar categoria implicitamente. |
| `Unidade` | Sim | Material.Unit (até 20). |
| `TipoControle` | Sim | `Quantity` ou `IndividualAsset`. Não inferir tipo a partir do nome do material. |
| `EstoqueMinimo` | Sim | Decimal não negativo, até 3 casas, máximo 999999999,999. |
| `SaldoInicial` | Sim | Decimal não negativo, até 3 casas, máximo 999999999,999; deve ser zero para `IndividualAsset`. Se positivo em `Quantity`, criar InventoryMovement `Entry`, quantidade positiva, ator autenticado e timestamp UTC. Nunca gravar saldo diretamente. |
| `ReferenciaSaldoInicial` | Condicional | Até 200; obrigatória quando SaldoInicial > 0 e persistida em InventoryMovement.Reference. |

## Validação e gravação implementadas

1. Upload limitado a 5 MiB, UTF-8 estrito, cabeçalho exato e ordenado, separador `;`, campos entre aspas e exatamente nove colunas por linha.
2. Validação por linha/campo para obrigatoriedade, comprimentos, tipo de controle, números pt-BR sem separador de milhar, limite/escala decimal, categoria ativa existente no setor TI e códigos duplicados no arquivo ou catálogo.
3. Saldo não negativo; `IndividualAsset` exige saldo zero; saldo quantitativo positivo exige referência. Linhas válidas são exibidas em uma tabela de prévia com código, nome, descrição, categoria, unidade, tipo, mínimo e saldo; erros são indicados pela linha física/campo. Qualquer erro bloqueia a gravação do lote.
4. A prévia não grava. Uma confirmação protegida por ASP.NET Core Data Protection vincula usuário, hash do conteúdo e expiração de 10 minutos. A confirmação exige reenviar exatamente o conteúdo validado; o servidor repete a validação antes de prosseguir.
5. Gravação em transação `Serializable`: materiais e entradas de saldo inicial são atômicos, com ator, setor TI e horário UTC. Saldo é representado apenas por movimento de ledger. Exceção ao gravar movimento reverte o catálogo.
6. Rejeição por lote inválido e rollback após falha no ledger são cobertos por testes HTTP com SQLite. Testes HTTP também confirmam que o token de prévia fica vinculado ao usuário e que outra identidade não pode confirmar o mesmo arquivo, e que os dois seletores de arquivo têm IDs distintos e labels associados. Um teste opt-in executa o caminho HTTP de prévia/commit e verifica material e movimento em PostgreSQL; ele precisa ser executado com migrations aplicadas em banco descartável.

## Portões para carga real

**Estado:** fluxo implementado e verificável localmente. Nenhuma carga de dados reais foi executada. Confirmar com o responsável pelos dados as categorias existentes, o CSV final, a contagem/reconciliação esperada e a autorização da carga; no ambiente operacional, exigir backup externo cuja restauração tenha sido testada e aprovação do plano da VM. O setor destino está codificado como `Id=1`, definido como TI no seed atual; revalidar essa identidade no ambiente de destino antes de usar o fluxo ali.
