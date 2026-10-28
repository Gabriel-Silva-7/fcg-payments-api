using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCG.Contracts;
using FCG.Payments.Persistence;
using FCG.Payments.Simulation;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FCG.Payments.Messaging;

// Transaction ownership stays with the consumer EF outbox (or an explicit local application scope).
public sealed class OrderProcessingHandler(PaymentsDbContext db, PaymentDecisionStore store,
    IOptions<PaymentSimulationOptions> options, TimeProvider clock, ILogger<OrderProcessingHandler> logger)
{
    public async Task HandleAsync(OrderPlacedEvent incoming, Guid? messageId, IPublishEndpoint publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Order processing requires an explicit transaction and transactional outbox.");
        // Serialize business identity independently of the middleware's finite MessageId inbox window.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"order:" + incoming.OrderId}, 0))", cancellationToken);
        var request = new PaymentSimulationRequest(incoming.OrderId, incoming.UserId, incoming.GameId, incoming.Price, incoming.PlacedAtUtc);
        var policy = options.Value.CreatePolicy();
        if (!await db.Decisions.AsNoTracking().AnyAsync(x => x.OrderId == incoming.OrderId, cancellationToken))
            await Task.Delay(policy.Delay, clock, cancellationToken);
        try
        {
            var now = clock.GetUtcNow(); now = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            var decision = (await store.GetOrCreateAsync(request, policy, now, cancellationToken)).Decision;
            var result = await db.Results.SingleOrDefaultAsync(x => x.OrderId == incoming.OrderId, cancellationToken);
            if (result is null)
            {
                var recorded = new PaymentProcessedEvent(decision.OrderId, decision.UserId, decision.GameId, decision.Price,
                    decision.Status.ToString(), decision.Reason, decision.ProcessedAtUtc);
                result = new PaymentResult { OrderId = incoming.OrderId, Payload = JsonSerializer.Serialize(recorded), CreatedAtUtc = now };
                db.Results.Add(result);
            }
            var original = JsonSerializer.Deserialize<PaymentProcessedEvent>(result.Payload)
                ?? throw new InvalidOperationException("The persisted result is unavailable.");
            await publication.Publish(original, publish => { publish.CorrelationId = incoming.OrderId; }, cancellationToken);
        }
        catch (PaymentDecisionConflictException)
        {
            var payload = JsonSerializer.Serialize(incoming);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var conflict = await db.MessageConflicts.SingleOrDefaultAsync(x => x.BusinessId == incoming.OrderId && x.PayloadHash == hash, cancellationToken);
            if (conflict is null)
            {
                conflict = new MessageConflict { Id = Guid.NewGuid(), Kind = "Order", BusinessId = incoming.OrderId,
                    MessageType = nameof(OrderPlacedEvent), TransportMessageId = messageId, Payload = payload,
                    PayloadHash = hash, Reason = "DivergentOrderRequest", FirstSeenAtUtc = clock.GetUtcNow() };
                db.MessageConflicts.Add(conflict);
            }
            conflict.Observations++; conflict.LastSeenAtUtc = clock.GetUtcNow();
            logger.LogWarning("PaymentOrderConflict {OrderId} {MessageId}", incoming.OrderId, messageId);
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
