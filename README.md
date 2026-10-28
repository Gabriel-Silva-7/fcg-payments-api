# FCG Payments

Serviço .NET10 que consome `OrderPlacedEvent` pelo RabbitMQ, simula pagamento e
publica `PaymentProcessedEvent`. Mantém banco próprio e não consulta bancos de
Users/Catalog/Notifications. Não executa cobrança real nem expõe pagamento HTTP.

## Executar e configurar

Requer SDK **10.0.400**, PostgreSQL16 e RabbitMQ. O global.json desativa roll-forward;
pacotes exatos e lockfiles protegem dependências. Para testes, Docker deve estar
acessível aos Testcontainers. Configure por ambiente ou provedor de segredos:

| Variável | Comportamento |
|---|---|
| `ConnectionStrings__Default` | Banco exclusivo Payments, obrigatório |
| `RabbitMq__Host` / `RabbitMq__Port` | Host obrigatório, sem URL com credenciais; porta padrão5672 |
| `RabbitMq__VirtualHost` | `/fcg`, obrigatório |
| `RabbitMq__Username` / `RabbitMq__Password` | Credencial própria obrigatória, sem padrão |
| `Queues__Orders` | Fila durável própria, padrão `fcg-payments-orders` |
| `Database__MigrateOnStartup` | `true` aplica migrations antes de atender; padrão `false` |
| `PaymentSimulation__ApprovalLimit` | BRL decimal invariante exato, padrão `500`; não negativo, cabe numeric(18,2) |
| `PaymentSimulation__Delay` | TimeSpan entre00:00:00 e00:05:00; padrão00:00:02 |

Use role PostgreSQL sem superuser/criação de bancos/roles e usuário AMQP sem tag
administrativa, limitado ao vhost. Migração exige criação dos objetos no banco
próprio. Configuração inválida impede startup; nunca vira rejeição de pagamento.
A topologia deve existir antes da publicação pelo Catalog.

```sh
dotnet restore FCG.Payments.sln --locked-mode
dotnet build FCG.Payments.sln -c Release --no-restore
dotnet test FCG.Payments.sln -c Release --no-restore
dotnet run --project src/FCG.Payments -c Release --no-build --no-launch-profile --urls http://127.0.0.1:8080
```

## Decisão, outbox e reentrega

MassTransit8.5.11 recebe o contrato canônico em `FCG.Contracts`, com enums/status
nomeados, BRL exato e UTC com seis casas decimais. Price até o limite é Approved,
sem Reason; acima é Rejected/AmountAboveLimit. Atraso é aplicado antes da primeira
decisão. `payment_decisions` preserva pedido original, decisão, data e política
aplicada. OrderId é único permanentemente.

O consumer EF outbox é dono da transação ReadCommitted, com inbox e saída durável.
A aplicação não abre transação aninhada. O armazenamento PAY02 participa dessa
transação usando savepoints; lock transacional por OrderId serializa comparação,
decisão, payload original em `Results` e stage do resultado. Falha antes do commit
desfaz todos. Ack de sucesso depende do commit e da entrega protegida do middleware.
O bus outbox também protege aplicação local em escopo/transação explícitos.

Reentrega equivalente com outro MessageId republica o payload de referência, sem
recalcular status/data/preço mesmo após mudança de limite. A janela de inbox é
30min; a deduplicação de negócio permanece no banco depois dela. IDs de transporte
podem mudar; CorrelationId é OrderId. Catalog e Notifications recebem cópias em
filas próprias, sem disputar uma única mensagem.

Mesmo OrderId com usuário/jogo/preço/PlacedAtUtc diferentes preserva a primeira
decisão e grava `MessageConflicts`: payload original recebido, tipo, correlação,
razão fixa, datas e contagem. Não publica outra decisão. Diagnóstico é tratamento
bem-sucedido somente depois do commit; erro ao gravá-lo provoca rollback/retry.
Não apagar decisão, inbox ou outbox para forçar reprocessamento.

Falha transitória tem tentativa inicial mais1s/5s/15s; esgotamento e contrato
inválido seguem para `_error` durável, sem resultado financeiro inventado. Após
corrigir a causa, redrive controlado para a fila original reaplica validação e
deduplicação normais. Replay operacional de conflitos será documentado na integração;
não há endpoint HTTP público de recuperação. Broker indisponível conserva saída
confirmada; reentrega/retomada entrega o resultado sem novo pedido.

## Saúde e privacidade

- `/health/live`: anônimo,200 Healthy, independente das dependências.
- `/health/ready`: consulta schema próprio/outbox e bus + conexão AMQP real;
  falha/degraded/unhealthy retorna503.
- `dotnet FCG.Payments.dll --healthcheck`: probe HTTP local8080, timeout3s, código0/1.

Pode iniciar com broker fora e retomar depois. Com migração habilitada, banco deve
estar disponível antes da partida. Checks têm descrições fixas sem exceções.
Fora de Development, logs brutos de MassTransit e eventos EF com exceção/stack
ficam suprimidos; conflitos registram somente IDs. Não habilitar logging sensível.
Proteja banco, backups e filas de erro; não publicar seus dumps na demonstração.

## Container e evidências

Dockerfile multi-stage com SDK10.0.400/runtime10.0.11 fixados por digest, porta8080
e usuário app/UID1654. Contexto inclui somente configuração de build e fontes.
Passe variáveis de ambiente por arquivo externo, fora do contexto:

```sh
docker --context orbstack build -t fcg-payments:local .
container=$(docker --context orbstack run -d -p 127.0.0.1::8080 \
  --env-file /caminho/externo/payments.env fcg-payments:local)
docker --context orbstack port "$container" 8080/tcp
docker --context orbstack exec "$container" dotnet FCG.Payments.dll --healthcheck
docker --context orbstack rm -f "$container"
```

Conexões precisam alcançar PostgreSQL/RabbitMQ pelo container. Testes usam esses
serviços reais para persistência, concorrência, atomicidade, transporte/fan-out,
reentrega com configuração alterada, falhas e destinos de erro. Provas de atomicidade
suspendem somente o entregador bus outbox; provas de transporte mantêm o consumidor
EF outbox real. Isso não comprova a jornada completa de compra, Compose ou Kubernetes.
