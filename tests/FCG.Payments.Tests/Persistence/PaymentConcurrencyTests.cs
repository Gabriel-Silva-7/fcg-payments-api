using System.Data;
using FCG.Payments.Persistence;
using FCG.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace FCG.Payments.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class PaymentConcurrencyTests(PostgresFixture database)
{
    private static readonly DateTimeOffset ProcessedAt = DateTimeOffset.Parse("2026-10-20T12:00:02.654321Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_connections_return_one_committed_winner_including_policy_time_and_valid_loser_transaction(bool external)
    {
        var request = PaymentDecisionTests.Request();
        var seed = PaymentDecisionTests.Request();
        await using (var context = database.Context())
            await new PaymentDecisionStore(context).GetOrCreateAsync(seed, new(), ProcessedAt);
        var barrier = new ConcurrentReadBarrier(request.OrderId);
        async Task<(PaymentDecisionWriteResult Result, int Backend)> Run(decimal limit, DateTimeOffset timestamp)
        {
            await using var context = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
                .UseNpgsql(database.ConnectionString).AddInterceptors(barrier).Options);
            context.Database.AutoSavepointsEnabled = false; // Prove the store's explicit recovery path.
            var retained = await context.Decisions.SingleAsync(value => value.OrderId == seed.OrderId);
            await context.Database.OpenConnectionAsync();
            var backend = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
            await using var transaction = external ? await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted) : null;
            var result = await new PaymentDecisionStore(context).GetOrCreateAsync(request, new(limit), timestamp);
            Assert.Same(retained, Assert.Single(context.Decisions.Local));
            Assert.Equal(EntityState.Unchanged, context.Entry(retained).State);
            // In the losing branch this SQL must execute AFTER real 23505/savepoint recovery.
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO payments_test_transaction_markers(id) VALUES ({Guid.NewGuid()})");
            if (transaction is not null) await transaction.CommitAsync();
            return (result, backend);
        }
        var writes = await Task.WhenAll(Run(500m, ProcessedAt), Run(700m, ProcessedAt.AddSeconds(1)));
        Assert.NotEqual(writes[0].Backend, writes[1].Backend);
        var winner = Assert.Single(writes, value => value.Result.Created).Result.Decision;
        var loser = Assert.Single(writes, value => !value.Result.Created).Result.Decision;
        Assert.Equal(1, barrier.UniqueCollisions);
        Assert.NotSame(winner, loser);
        Assert.Equal(winner.OrderId, loser.OrderId);
        Assert.Equal(winner.UserId, loser.UserId);
        Assert.Equal(winner.GameId, loser.GameId);
        Assert.Equal(winner.Price, loser.Price);
        Assert.Equal(winner.PlacedAtUtc, loser.PlacedAtUtc);
        Assert.Equal(winner.Status, loser.Status);
        Assert.Equal(winner.Reason, loser.Reason);
        Assert.Equal(winner.ProcessedAtUtc, loser.ProcessedAtUtc);
        Assert.Equal(winner.ApprovalLimit, loser.ApprovalLimit);
        Assert.Equal(winner.DelayTicks, loser.DelayTicks);
        Assert.Equal(winner.PolicyVersion, loser.PolicyVersion);
        await using var reader = database.Context();
        var durable = await reader.Decisions.SingleAsync(value => value.OrderId == request.OrderId);
        Assert.Equal(winner.ApprovalLimit, durable.ApprovalLimit);
        Assert.Equal(winner.Status, durable.Status);
        Assert.Equal(winner.ProcessedAtUtc, durable.ProcessedAtUtc);
        Assert.Equal(1, await reader.Decisions.CountAsync(value => value.OrderId == request.OrderId));
    }

    [Fact]
    public async Task Rolled_back_provisional_decision_does_not_win_against_a_later_committed_decision()
    {
        var request = PaymentDecisionTests.Request();
        await using var first = database.Context();
        await using var transaction = await first.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var provisional = await new PaymentDecisionStore(first).GetOrCreateAsync(request, new(500m), ProcessedAt);
        Assert.True(provisional.Created);
        await transaction.RollbackAsync();
        await using var other = database.Context();
        var committed = await new PaymentDecisionStore(other).GetOrCreateAsync(request, new(700m), ProcessedAt.AddSeconds(1));
        Assert.True(committed.Created);
        await using var restarted = database.Context();
        var replay = await new PaymentDecisionStore(restarted).GetOrCreateAsync(request, new(500m), ProcessedAt.AddDays(1));
        Assert.False(replay.Created);
        Assert.Equal(PaymentSimulationStatus.Approved, replay.Decision.Status);
        Assert.Equal(700m, replay.Decision.ApprovalLimit);
        Assert.Equal(ProcessedAt.AddSeconds(1), replay.Decision.ProcessedAtUtc);
    }

    [Fact]
    public async Task Store_preserves_existing_tracked_entities_and_rejects_pending_changes()
    {
        var seed = PaymentDecisionTests.Request();
        await using var context = database.Context();
        var store = new PaymentDecisionStore(context);
        var existing = await store.GetOrCreateAsync(seed, new(), ProcessedAt);
        context.Attach(existing.Decision);
        await store.GetOrCreateAsync(PaymentDecisionTests.Request(), new(), ProcessedAt);
        Assert.Equal(EntityState.Unchanged, context.Entry(existing.Decision).State);
        context.Entry(existing.Decision).Property(value => value.Price).CurrentValue = 1m;
        var request = PaymentDecisionTests.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetOrCreateAsync(request, new(), ProcessedAt));
        await using var reader = database.Context();
        Assert.False(await reader.Decisions.AnyAsync(value => value.OrderId == request.OrderId));
        Assert.Equal(600m, (await reader.Decisions.SingleAsync(value => value.OrderId == seed.OrderId)).Price);
    }

    [Theory]
    [InlineData("23514")]
    [InlineData("23505")]
    public async Task Unrelated_database_constraint_error_propagates_without_claiming_duplicate(string sqlState)
    {
        var request = PaymentDecisionTests.Request();
        await using var context = database.Context();
        // An actual application-owned DB trigger provides a different constraint failure.
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION payments_test_reject_test_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.policy_version = 'amount-limit-v1' THEN
                    RAISE EXCEPTION 'test integrity failure' USING ERRCODE = '23514', CONSTRAINT = 'payments_test_test_constraint', TABLE = 'payment_decisions', SCHEMA = 'public';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER payments_test_test_constraint BEFORE INSERT ON public.payment_decisions
            FOR EACH ROW EXECUTE FUNCTION payments_test_reject_test_insert();
            """.Replace("23514", sqlState, StringComparison.Ordinal));
        try
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => new PaymentDecisionStore(context).GetOrCreateAsync(request, new(), ProcessedAt));
            var postgres = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(sqlState, postgres.SqlState);
            Assert.Equal("payments_test_test_constraint", postgres.ConstraintName);
            await using var reader = database.Context();
            Assert.False(await reader.Decisions.AnyAsync(value => value.OrderId == request.OrderId));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER payments_test_test_constraint ON public.payment_decisions; DROP FUNCTION payments_test_reject_test_insert()");
        }
    }
}
