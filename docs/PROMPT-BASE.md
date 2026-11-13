# Prompt refinado — Sistema de Gestão de Materiais

Atue como engenheiro de software sênior. Desenvolva um sistema web de Gestão de Materiais em C# com ASP.NET Core/.NET, dentro desta pasta, usando arquitetura simples, sustentável e testável. Antes de implementar cada sprint, inspecione o sistema de Gestão de Brindes em `../GestaoPHPLegado-main`: leia a documentação de UI/UX, examine as telas, estilos, componentes e fluxos existentes. Use-o como referência visual e de experiência, sem acoplar o novo sistema ao PHP.

## Produto

O sistema controla materiais corporativos (por exemplo, mouse, teclado, headset e monitor). A primeira implantação é um piloto do setor de TI. A arquitetura deve suportar múltiplos setores e garantir isolamento: cada usuário só consulta e opera o estoque dos setores que lhe foram atribuídos. Implemente essa regra no servidor e teste tentativas de acesso fora do escopo; não confie apenas em filtros da interface.

## Princípios obrigatórios

- Usar .NET 10 LTS. O build/teste deve rodar no GitHub Actions; o SDK local é opcional. Não inventar dependências e justificar cada pacote.
- Persistência relacional com migrations; nenhuma persistência efêmera para dados operacionais.
- Toda alteração de estoque deve ser uma movimentação auditável. Saldo não pode ser editado manualmente.
- Criar movimentos e atualizar saldo atomicamente; impedir saldo negativo, concorrência inconsistente e requisições duplicadas.
- Guardar autor, setor, tipo, quantidade, horário, justificativa e referência pertinente em cada movimento.
- Autenticar e autorizar no backend, validar entradas, proteger sessão/CSRF e não registrar segredos em logs ou versionamento.
- Interface responsiva e acessível, inspirada no design system do sistema de brindes: cores, tipografia, hierarquia, navegação, componentes e estados devem ser inspecionados no código real antes de serem reproduzidos. Não presumir cores ou frameworks.
- Tratar estados de carregamento, vazio, sucesso e erro; suportar desktop e tablet.
- Entregar testes e executar build/testes relevantes antes de declarar cada sprint concluída.
- Fazer mudanças somente na pasta `NovoSistemaMateriais`; não alterar o legado.
- Hospedar em VM Azure Ubuntu 24.04 com PostgreSQL 16 local. Manter o banco escutando apenas em localhost e não expor a porta 5432. Documentar build, testes, publicação e atualização controlada; não presumir deploy automático. Guardar senha do banco e chaves fora do repositório; preparar domínio/TLS e backup antes do piloto.

## Escopo inicial

Catálogo de materiais e categorias, estoque segregado por setor, entrada, saída por consumo definitivo ou empréstimo com devolução, ajuste com justificativa, histórico, níveis mínimos, busca e painel de estoque baixo. Cada material poderá ser controlado por quantidade ou individualmente por série/patrimônio. Começar com setor TI. Administrador e gestor podem executar todas as operações; assistente e auxiliar podem operar, exceto ajustes, que exigem aprovação de gestor antes de alterar saldo. Usar PostgreSQL 16 na VM Ubuntu 24.04. Preparar importação inicial por CSV validado e pipeline GitHub Actions para build/teste e publicação controlada após aprovação. Não implementar ERP, notificações ou expansão produtiva de setores sem priorização explícita.

## Plano de execução

Trabalhe em sprints conforme `docs/SPRINTS.md` e continue a partir da primeira sprint incompleta. As decisões do piloto já estão registradas nesse documento. Implemente uma sprint por vez, sem commits não solicitados, com testes, documentação de execução e relatório curto de entregue/verificado/pendente.

## Critérios de aceite do MVP

1. Usuário autorizado de TI consegue cadastrar materiais, registrar entradas/saídas/ajustes e consultar saldo/histórico.
2. Nenhuma saída pode levar o saldo abaixo de zero.
3. Movimentos preservam autoria e justificativa e não podem ser apagados para ocultar histórico.
4. Atualizações simultâneas não corrompem saldo nem duplicam movimento.
5. O backend impede acesso a dados de outro setor mesmo com ID alterado diretamente.
6. Dashboard e relatórios respeitam o setor autorizado.
7. Interface mantém coerência comprovável com a referência visual real e funciona em tablet.
8. Build e testes automatizados passam; instruções de configuração e execução são reproduzíveis.
