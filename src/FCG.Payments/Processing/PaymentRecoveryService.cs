using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCG.Contracts;
using FCG.Payments.Messaging;
using FCG.Payments.Persistence;
using FCG.Payments.Simulation;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace FCG.Payments.Processing;

public sealed record PaymentRecoveryResult(string Outcome, string? Reason = null, IReadOnlyList<string>? PlannedEffects = null)
{
    public int ExitCode => Outcome == "NotFound" ? 3 : Outcome == "Quarantined" ? 4 : 0;
}

public sealed class PaymentRecoveryService(PaymentsDbContext db, TimeProvider clock, IServiceProvider services)
{
    public async Task<PaymentRecoveryResult> PreviewAsync(Guid id, CancellationToken ct)
    {
        RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var conflict = await db.MessageConflicts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (conflict is null) return new("NotFound");
        if (conflict.Resolved) return new("AlreadyResolved");
        var (message, reason) = ReadStored(conflict); if (message is null) return new("Quarantined", reason);
        var decision = await db.Decisions.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == message.OrderId, ct);
        var result = await db.Results.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == message.OrderId, ct);
        return Decide(message, decision, result);
    }

    public async Task<PaymentRecoveryResult> ReplayAsync(Guid id, CancellationToken ct)
    {
        RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Lock("replay:" + id, ct);
        var conflict = await db.MessageConflicts.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (conflict is null) return new("NotFound");
        PaymentRecoveryResult outcome;
        if (conflict.Resolved) outcome = new("AlreadyResolved");
        else
        {
            var (message, reason) = ReadStored(conflict);
            if (message is null) outcome = new("Quarantined", reason);
            else
            {
                await Lock("order:" + message.OrderId, ct);
                // An operator cannot delete/change the known winner while this replay calls the original application.
                var decision = await db.Decisions.FromSqlInterpolated($"SELECT * FROM public.payment_decisions WHERE order_id={message.OrderId} FOR SHARE")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
                var result = await db.Results.FromSqlInterpolated($"SELECT * FROM \"Results\" WHERE \"OrderId\"={message.OrderId} FOR SHARE")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
                outcome = Decide(message, decision, result);
                if (outcome.Outcome == "ReadyToStage")
                {
                    // Tracker still has no pending writes when the decision store validates its explicit transaction/savepoint contract.
                    await services.GetRequiredService<OrderProcessingHandler>().HandleAsync(message, conflict.TransportMessageId,
                        services.GetRequiredService<IPublishEndpoint>(), ct);
                    conflict.Resolved = true; conflict.ResolvedAtUtc = clock.GetUtcNow();
                    outcome = new("StagedOriginalResult");
                }
            }
        }
        db.PaymentRecoveryAttempts.Add(new PaymentRecoveryAttempt { Id = Guid.NewGuid(), ConflictId = id,
            AttemptedAtUtc = clock.GetUtcNow(), Outcome = outcome.Outcome, Reason = outcome.Reason });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return outcome;
    }

    private static PaymentRecoveryResult Decide(OrderPlacedEvent message, PersistedPaymentDecision? decision, PaymentResult? result)
    {
        if (decision is null) return new("Quarantined", "PaymentDecisionNotFound");
        var request = new PaymentSimulationRequest(message.OrderId, message.UserId, message.GameId, message.Price, message.PlacedAtUtc);
        if (PaymentDecisionStore.DifferingFields(decision, request).Count != 0) return new("Quarantined", "DivergentOrderRequest");
        if (result is not null && PaymentResultIntegrity.Read(decision, result) is null) return new("Quarantined", "PersistedResultDoesNotMatchDecision");
        return new("ReadyToStage", PlannedEffects: result is null ? ["RebuildResultFromOriginalDecision", "StageOriginalResult"] : ["StageOriginalResult"]);
    }
    private static (OrderPlacedEvent? Message, string? Reason) ReadStored(MessageConflict conflict)
    {
        if (conflict.Kind != "Order" || conflict.MessageType != nameof(OrderPlacedEvent)) return (null, "UnsupportedMessageType");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conflict.Payload)));
        if (hash != conflict.PayloadHash) return (null, "InvalidStoredPayload");
        try
        {
            var message = JsonSerializer.Deserialize<OrderPlacedEvent>(conflict.Payload);
            return message is not null && message.OrderId == conflict.BusinessId && conflict.EventId == Guid.Empty && conflict.EventVersion is null
                ? (message, null) : (null, "InvalidStoredPayload");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return (null, "InvalidStoredPayload"); }
    }
    private Task Lock(string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    private void RequireOwnTransaction() { if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Payment recovery owns its transaction."); }
}
