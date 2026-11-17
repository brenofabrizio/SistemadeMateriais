# Manual rápido — Piloto de Gestão de Materiais

**Estado:** manual preparado para o piloto. O usuário informou em 17/11/2026 que a equipe de TI já realizou revisão e homologação; esta anotação registra a confirmação recebida, não substitui evidência assinada nem autorização operacional para produção.

## Acesso e escopo

1. Acesse o endereço informado pelo administrador e entre com a conta provisionada para você. Não há cadastro público.
2. O piloto opera no setor de TI. Não tente acessar dados de outros setores por URL nem compartilhe sua conta.
3. Use **Sair** ao terminar em um computador compartilhado. Não salve senhas em arquivos ou mensagens.

## Preparar o catálogo

1. Em **Categorias**, crie a categoria necessária antes de cadastrar materiais.
2. Em **Materiais**, informe código, nome, categoria, unidade de medida e estoque mínimo.
3. Escolha o controle correto:
   - **Por quantidade**: itens consumíveis ou controlados em saldo agregado.
   - **Unidade individual**: itens que precisam ser identificados por número de série ou patrimônio.
4. Consulte saldo, situação, detalhes e filtros na lista de materiais. Use **Editar** para corrigir dados do catálogo; a movimentação de estoque não é feita nessa tela.
5. Depois da primeira movimentação ou do cadastro de um ativo, o tipo de controle fica bloqueado para evitar reinterpretar o histórico. Se a classificação estiver incorreta, não tente contornar criando outro lançamento; peça ao gestor uma correção planejada.

## Materiais controlados por quantidade

- **Registrar entrada:** selecione o material, informe a quantidade recebida e, se houver, nota fiscal, pedido ou referência. Confirme em **Registrar entrada**.
- **Registrar consumo:** selecione o material, informe a quantidade consumida e a justificativa/referência. A operação é recusada se não houver saldo suficiente.
- **Empréstimo:** em **Empréstimos**, selecione o material, responsável, quantidade e, opcionalmente, previsão de devolução e referência. O saldo em aberto aparece na lista.
- **Devolução:** informe a quantidade a devolver na linha do empréstimo em aberto e escolha **Registrar devolução**. Devoluções parciais são permitidas; não ultrapasse a quantidade em aberto.

## Ativos individualizados

1. Cadastre o material com controle **Unidade individual**.
2. Em **Ativos individualizados**, registre cada unidade recebida com número de série ou patrimônio único e uma referência opcional.
3. Use as ações da unidade para emprestar, registrar devolução ou consumo. O estado e o movimento correspondente são gravados juntos.
4. Não use as telas de entrada, consumo ou empréstimo por quantidade para movimentar unidades individualizadas.

## Ajustes de estoque

Registre uma diferença positiva ou negativa somente para corrigir uma contagem, sempre com justificativa. Gestores e administradores podem aplicar o ajuste diretamente. Assistentes e auxiliares enviam um pedido pendente para decisão de gestor ou administrador. O solicitante não pode aprovar o próprio pedido; pedidos recusados não alteram o saldo.

## Consulta e boas práticas

- Use **Histórico** para consultar movimentações e filtros disponíveis; o saldo é derivado dos lançamentos auditáveis.
- O painel destaca materiais abaixo do mínimo e movimentos recentes.
- Não altere saldo diretamente, não compartilhe contas e não registre um segundo lançamento para “corrigir” um erro sem antes conferir o histórico e solicitar orientação ao gestor.
- Se uma operação falhar, leia a mensagem apresentada e verifique o histórico antes de tentar novamente. Em caso de conflito, atualize a página e confirme se o lançamento já foi gravado.
- Solicite ao administrador correções de acesso. Relate ao gestor divergências de quantidade, identificação ou movimentação para que sejam verificadas antes de novo lançamento.

## Antes do piloto real

Manter registro da revisão/homologação da TI e concluir a validação do procedimento de backup/restauração, implantação e permissões operacionais antes de tratar o ambiente implantado como produção.
