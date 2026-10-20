# FCG Payments

Host inicial em ASP.NET Core, sem endpoints de negócio. Há um projeto web de produção e um projeto de testes do host.

## Requisitos e execução local

Instale o SDK .NET **10.0.400**. O `global.json` desativa roll-forward; confirme que `dotnet --version` apresenta essa versão na raiz do repositório. Use `dotnet` no PATH, ou substitua o executável nos comandos pela instalação desejada.

```sh
dotnet restore FCG.Payments.sln --locked-mode
dotnet build FCG.Payments.sln -c Release --no-restore
dotnet test FCG.Payments.sln -c Release --no-restore
dotnet run --project src/FCG.Payments -c Release --no-build --urls http://127.0.0.1:8080
```

Pacotes têm versões exatas em `Directory.Packages.props`; os lockfiles preservam as dependências transitivas. Nullable, implicit usings e warnings-as-errors estão habilitados para net10.0.

## Saúde e configuração

- `GET /health/live`: 200, texto `Healthy`. Indica que o processo responde, sem executar checks de dependências.
- `GET /health/ready`: 200, texto `Healthy` neste bootstrap. Executa somente health checks com tag `ready`; resultados Degraded ou Unhealthy retornam 503.

Ainda não há banco, broker, consumidores nem autenticação conectados. Readiness comprova apenas o bootstrap nesta etapa; os incrementos de infraestrutura acrescentarão checks reais.

No container, `ASPNETCORE_HTTP_PORTS=8080` define a porta e o processo usa o usuário `app` (UID 1654). O argumento `--healthcheck` executa somente um probe HTTP .NET contra `http://127.0.0.1:8080/health/ready`, com timeout de três segundos: código 0 para resposta de sucesso e 1 para erro, timeout ou HTTP sem sucesso. O health check acompanha a mesma porta; preserve 8080 no container.

## Container local

O build multi-stage usa SDK 10.0.400 e runtime 10.0.11, com digests fixados. O contexto Docker inclui somente a configuração de build e `src`, excluindo bin/obj e arquivos de ambiente.

Com Docker e contexto OrbStack disponíveis, execute na raiz. A publicação usa somente loopback e porta aleatória; `port` informa a porta atribuída:

```sh
docker --context orbstack build -t fcg-payments:local .
container=$(docker --context orbstack run -d -p 127.0.0.1::8080 fcg-payments:local)
docker --context orbstack port "$container" 8080/tcp
docker --context orbstack inspect --format '{{.State.Health.Status}}' "$container"
docker --context orbstack exec "$container" cat /proc/1/status
# Use a porta retornada acima para consultar /health/live e /health/ready.
# Após a verificação, remova somente o container criado:
docker --context orbstack rm -f "$container"
```

O estado healthy aparece após o probe inicial. Os testes do host verificam isolamento live/ready e o mapeamento de checks falhos; os testes do probe usam servidores HTTP reais locais para sucesso, 503, indisponibilidade e timeout.

Os manifestos Kubernetes serão adicionados na etapa de implantação; veja [k8s](k8s/README.md).

## Simulação de pagamento

`FCG.Payments.Simulation.PaymentSimulator.Evaluate(request, policy, processedAtUtc)` calcula uma decisão BRL a partir de `PaymentSimulationRequest(OrderId, UserId, GameId, Price, PlacedAtUtc)`. Os três GUIDs devem ser não vazios. Price e o limite aceitam de 0 até 9999999999999999.99, sem fração de centavo e sem arredondamento (`numeric(18,2)`). Instantes usam `DateTimeOffset`, offset UTC zero e precisão máxima de microssegundos; o chamador fornece ProcessedAtUtc explicitamente.

Com a política padrão, preços 0 e 500 resultam em `Approved` com `Reason = null`; 500.01 resulta em `Rejected` com `Reason = "AmountAboveLimit"`. A decisão conserva os dados do pedido, o instante fornecido e a política imutável aplicada. Dados inválidos falham com exceção de argumento antes de gerar decisão.

`PaymentSimulationOptions` oferece estas chaves, para integração pelo futuro adaptador de processamento:

| Chave | Padrão | Valores válidos |
|---|---|---|
| `PaymentSimulation:ApprovalLimit` | `500.00` | BRL não negativo compatível com `numeric(18,2)`, sem fração de centavo |
| `PaymentSimulation:Delay` | `00:00:02` | `TimeSpan` entre `00:00:00` e `00:05:00`, inclusive |

`CreatePolicy()` valida e produz um snapshot imutável. `PaymentSimulationOptionsValidator` implementa `IValidateOptions<PaymentSimulationOptions>` e informa todas as chaves inválidas; o adaptador deverá registrá-lo com `ValidateOnStart` para impedir inicialização com configuração inválida. Este host ainda não conecta essas opções. A regra retorna sincronicamente, sem esperar Delay; a espera será responsabilidade do adaptador. Use Delay zero em testes do processamento.

A simulação calcula em memória. Persistir a primeira decisão por OrderId, reaproveitar resultados em reentrega, consumir/publicar eventos e garantir transação são responsabilidades dos incrementos de persistência e transporte.

```sh
dotnet test FCG.Payments.sln -c Release --no-restore --filter FullyQualifiedName~Simulation
```
