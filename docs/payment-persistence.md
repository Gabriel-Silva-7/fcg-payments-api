# Decisões de pagamento persistidas

`FCG.Payments.Persistence` armazena a primeira decisão confirmada de cada
`OrderId` no PostgreSQL próprio de Payments. O pedido completo, o resultado,
o motivo, o instante de processamento e a política aplicada ficam registrados.
Um pedido de 600 rejeitado sob limite 500 continua rejeitado depois de uma
mudança do limite para 700 e de recriar o contexto da aplicação.

`PaymentDecisionStore.GetOrCreateAsync(request, policy, processedAtUtc, ct)`
retorna `PaymentDecisionWriteResult`, contendo `Decision` e `Created`.
Uma repetição equivalente lê o registro existente sem recalcular a regra nem
usar o novo instante. A equivalência compara `OrderId`, `UserId`, `GameId`,
`Price` e `PlacedAtUtc`. Um OrderId diferente representa outro pedido.
`PaymentDecisionConflictException` expõe o OrderId e `DifferingFields` quando
algum campo diverge. O registro original permanece preservado. O chamador pode
usar esse erro tipado para registrar seu diagnóstico de recepção.

A política persistida contém `ApprovalLimit`, `DelayTicks` e
`PolicyVersion=amount-limit-v1`. Preços e limites são BRL `numeric(18,2)`;
a entrada deve ser não negativa, ter até duas casas decimais e caber em
9999999999999999.99, sem arredondamento. GUIDs vazios são inválidos.
Os instantes devem ter offset UTC zero e precisão máxima de microssegundos,
compatível com `timestamp with time zone`. O request é validado também nas
reentregas, antes da consulta; representar o mesmo instante com offset -03:00
continua inválido. O `processedAtUtc` fornecido só é usado para decisões novas.

## Transação do chamador

O contexto recebe `DbContextOptions<PaymentsDbContext>` configuradas pelo
chamador com `UseNpgsql`. Não há conexão implícita no contexto.
Sem transação externa, o retorno de uma criação sucede o commit da inserção.
Com `BeginTransaction` ou `UseTransaction`, o store participa da transação
existente e o chamador controla o commit ou rollback. Nesse caso `Created=true`
significa que houve inserção dentro da unidade de trabalho; a decisão ainda
não está confirmada até o commit externo.

```csharp
await using var context = new PaymentsDbContext(options);
await using var transaction = await context.Database.BeginTransactionAsync(
    System.Data.IsolationLevel.ReadCommitted, cancellationToken);

var result = await new PaymentDecisionStore(context).GetOrCreateAsync(
    request, policy, processedAtUtc, cancellationToken);

// Gravar a intenção associada nesta mesma conexão/transação.
// Se usar outro DbContext, compartilhar DbConnection e DbTransaction via UseTransaction.
await context.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

O store exige transação externa **ReadCommitted**, com savepoints. Outros
isolamentos e `TransactionScope` ambient são recusados. Cada chamada precisa de
contexto exclusivo, sem mudanças pendentes na entrada. O chamador pode salvar
alterações anteriores dentro da mesma transação e inserir as intenções depois
da decisão. Entidades já rastreadas e sem alterações são preservadas. Não
compartilhar um DbContext entre threads.

Em concorrência, a PK `PK_payment_decisions` decide o vencedor. Apenas o erro
PostgreSQL 23505 dessa PK, tabela e schema é tratado como colisão de OrderId.
O perdedor reverte seu savepoint, desanexa o candidato e relê o vencedor com
`AsNoTracking`, em transação válida. Outros erros propagam; o chamador deve
reverter a transação quando a operação falha. Não publicar o cálculo provisório.

A integração de transporte/outbox deve configurar explicitamente ReadCommitted.
No [MassTransit 8.5.11](https://github.com/MassTransit/MassTransit/blob/5d6a89578fe9bc5ec17df0fb3d307a0b16cde2a7/src/Persistence/MassTransit.EntityFrameworkCoreIntegration/Configuration/Configuration/EntityFrameworkOutboxConfigurator.cs#L27) o default da outbox EF é RepeatableRead; o adapter precisará
configurar `o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted`.
A atomicidade demonstrada aqui é decisão + escrita associada numa transação
PostgreSQL; a integração com a outbox será feita pelo consumidor.

## Migration e testes

A migration inicial fica em `Persistence/Migrations`. Uma instância configurada
de `PaymentsDbContext` aplica `await context.Database.MigrateAsync(ct)` antes
do uso. O factory de design existe para gerar migrations sem iniciar o host;
comandos de atualização devem fornecer a conexão explicitamente. O host registra o
`PaymentsDbContext` e aplica as migrations no startup quando `Database__MigrateOnStartup=true`.

Com SDK 10.0.400 no diretório do projeto:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
DOCKER_HOST=unix:///caminho/para/docker.sock dotnet test -c Release --no-restore
```

Os testes usam PostgreSQL 16 fixado por digest, container e volume exclusivos.
O superuser de bootstrap cria uma role real `fcg_payments` sem superuser,
CREATEDB ou CREATEROLE. Essa role migra e usa o próprio banco e recebe acesso
negado ao banco alheio. A fixture remove apenas seus recursos ao terminar.
