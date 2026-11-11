using System.Transactions;
using IsolationLevel = System.Data.IsolationLevel;
using FCG.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace FCG.Payments.Persistence;

/// <summary>Preserves the first committed decision. External transactions retain commit ownership.</summary>
public sealed class PaymentDecisionStore(PaymentsDbContext context)
{
    private readonly PaymentsDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private const string InsertSavepoint = "payment_decision_insert";

    public async Task<PaymentDecisionWriteResult> GetOrCreateAsync(PaymentSimulationRequest request,
        PaymentSimulationPolicy policy, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
    {
        // DateTimeOffset equality alone would accept a non-UTC replay of the same instant.
        PaymentSimulator.ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = RequireUsableContext();
        var existing = await FindAsync(request.OrderId, cancellationToken);
        if (existing is not null) return ExistingResult(existing, request);

        var candidate = new PersistedPaymentDecision(PaymentSimulator.Evaluate(request, policy, processedAtUtc));
        if (transaction is not null)
            await transaction.CreateSavepointAsync(InsertSavepoint, cancellationToken);
        context.Decisions.Add(candidate);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsOrderCollision(error))
        {
            if (transaction is not null)
            {
                // Recover PostgreSQL's failed statement state before issuing any SELECT.
                await transaction.RollbackToSavepointAsync(InsertSavepoint, CancellationToken.None);
                await transaction.ReleaseSavepointAsync(InsertSavepoint, CancellationToken.None);
            }
            context.Entry(candidate).State = EntityState.Detached;
            // ReadCommitted gets a fresh snapshot, and AsNoTracking cannot return the losing candidate.
            var winner = await FindAsync(request.OrderId, cancellationToken)
                ?? throw new InvalidOperationException("The committed payment decision disappeared after an OrderId collision.", error);
            return ExistingResult(winner, request);
        }
        if (transaction is not null)
            await transaction.ReleaseSavepointAsync(InsertSavepoint, cancellationToken);
        context.Entry(candidate).State = EntityState.Detached;
        return new(candidate, true);
    }

    private IDbContextTransaction? RequireUsableContext()
    {
        if (context.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Save pending changes before the payment decision; append associated writes afterwards in the same transaction.");
        if (System.Transactions.Transaction.Current is not null || context.Database.GetEnlistedTransaction() is not null)
            throw new InvalidOperationException("Use BeginTransaction or UseTransaction with explicit ReadCommitted isolation, not an ambient transaction.");
        var transaction = context.Database.CurrentTransaction;
        if (transaction is not null && (transaction.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted || !transaction.SupportsSavepoints))
            throw new InvalidOperationException("Payment decisions require an external ReadCommitted transaction with savepoint support.");
        return transaction;
    }

    private Task<PersistedPaymentDecision?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        context.Decisions.AsNoTracking().SingleOrDefaultAsync(value => value.OrderId == orderId, cancellationToken);

    private static bool IsOrderCollision(DbUpdateException error) => error.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.UniqueViolation,
        SchemaName: "public",
        TableName: "payment_decisions",
        ConstraintName: "PK_payment_decisions"
    };

    internal static IReadOnlyList<string> DifferingFields(PersistedPaymentDecision existing, PaymentSimulationRequest request)
    {
        List<string> different = [];
        if (existing.UserId != request.UserId) different.Add(nameof(request.UserId));
        if (existing.GameId != request.GameId) different.Add(nameof(request.GameId));
        if (existing.Price != request.Price) different.Add(nameof(request.Price));
        if (existing.PlacedAtUtc != request.PlacedAtUtc) different.Add(nameof(request.PlacedAtUtc));
        return different.AsReadOnly();
    }

    private static PaymentDecisionWriteResult ExistingResult(PersistedPaymentDecision existing, PaymentSimulationRequest request)
    {
        var different = DifferingFields(existing, request);
        if (different.Count != 0) throw new PaymentDecisionConflictException(request.OrderId, different);
        return new(existing, false);
    }
}
