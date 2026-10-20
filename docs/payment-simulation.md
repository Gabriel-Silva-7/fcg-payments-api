# Simulação de pagamento

`FCG.Payments.Simulation.PaymentSimulator.Evaluate(request, policy, processedAtUtc)` calcula uma decisão BRL a partir de `PaymentSimulationRequest(OrderId, UserId, GameId, Price, PlacedAtUtc)`. Os três GUIDs devem ser não vazios. Price e o limite aceitam de 0 até 9999999999999999.99, sem fração de centavo e sem arredondamento (`numeric(18,2)`). Instantes usam `DateTimeOffset`, offset UTC zero e precisão máxima de microssegundos; o chamador fornece ProcessedAtUtc explicitamente.

Com a política padrão, preços 0 e 500 resultam em `Approved` com `Reason = null`; 500.01 resulta em `Rejected` com `Reason = "AmountAboveLimit"`. A decisão conserva os dados do pedido, o instante fornecido e a política imutável aplicada. Dados inválidos falham com exceção de argumento antes de gerar decisão.

`PaymentSimulationOptions` oferece estas chaves, para integração pelo futuro adaptador de processamento:

| Chave | Padrão | Valores válidos |
|---|---|---|
| `PaymentSimulation:ApprovalLimit` | `500.00` | BRL não negativo compatível com `numeric(18,2)`, sem fração de centavo |
| `PaymentSimulation:Delay` | `00:00:02` | `TimeSpan` entre `00:00:00` e `00:05:00`, inclusive |

`ApprovalLimit` é texto nas opções para preservar o valor bruto de `IConfiguration` antes de converter para decimal. O formato de configuração é invariante: dígitos ASCII, ponto decimal opcional com dígitos; sem sinal, espaços, vírgula, separador de milhares ou expoente. Zeros redundantes são aceitos (`500.000`), mas uma fração não nula além de centavos é recusada mesmo se tão pequena que `decimal` a arredondaria. A política e a regra continuam usando decimal. Exemplo programático: `new PaymentSimulationOptions { ApprovalLimit = "42.50", Delay = TimeSpan.Zero }.CreatePolicy()`.

`CreatePolicy()` valida e produz um snapshot imutável. `PaymentSimulationOptionsValidator` implementa `IValidateOptions<PaymentSimulationOptions>` e informa todas as chaves inválidas; o adaptador deverá registrá-lo com `ValidateOnStart` para impedir inicialização com configuração inválida. Este host ainda não conecta essas opções. A regra retorna sincronicamente, sem esperar Delay; a espera será responsabilidade do adaptador. Use Delay zero em testes do processamento.

A simulação calcula em memória. Persistir a primeira decisão por OrderId, reaproveitar resultados em reentrega, consumir/publicar eventos e garantir transação são responsabilidades dos incrementos de persistência e transporte.

```sh
dotnet test FCG.Payments.sln -c Release --no-restore --filter FullyQualifiedName~Simulation
```
