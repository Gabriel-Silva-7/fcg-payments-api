using System.Data;
using FCG.Payments.Persistence;
using FCG.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace FCG.Payments.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class PaymentDecisionTests(PostgresFixture database)
{
    private static readonly DateTimeOffset PlacedAt = DateTimeOffset.Parse("2026-10-20T12:00:00.123456Z");
    private static readonly DateTimeOffset ProcessedAt = DateTimeOffset.Parse("2026-10-20T12:00:02.654321Z");
    internal static PaymentSimulationRequest Request(decimal price = 600m) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), price, PlacedAt);

    [Fact]
    public async Task Rejected_600_under_500_survives_restart_and_limit_700_with_original_policy_and_time()
    {
        var request = Request();
        await using (var first = database.Context())
        {
            var created = await new PaymentDecisionStore(first).GetOrCreateAsync(request, new(500m), ProcessedAt);
            Assert.True(created.Created);
            Assert.Equal(PaymentSimulationStatus.Rejected, created.Decision.Status);
        }
        await using var restarted = database.Context();
        var replay = await new PaymentDecisionStore(restarted).GetOrCreateAsync(request, new(700m, TimeSpan.FromSeconds(4)), ProcessedAt.AddDays(1));
        Assert.False(replay.Created);
        Assert.Equal(PaymentSimulationStatus.Rejected, replay.Decision.Status);
        Assert.Equal("AmountAboveLimit", replay.Decision.Reason);
        Assert.Equal(ProcessedAt, replay.Decision.ProcessedAtUtc);
        Assert.Equal(500m, replay.Decision.ApprovalLimit);
        Assert.Equal(20000000L, replay.Decision.DelayTicks);
        Assert.Equal("amount-limit-v1", replay.Decision.PolicyVersion);
        Assert.Equal(request.OrderId, replay.Decision.OrderId);
        Assert.Equal(request.UserId, replay.Decision.UserId);
        Assert.Equal(request.GameId, replay.Decision.GameId);
        Assert.Equal(600m, replay.Decision.Price);
        Assert.Equal(PlacedAt, replay.Decision.PlacedAtUtc);
        Assert.Equal(TimeSpan.Zero, replay.Decision.PlacedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, replay.Decision.ProcessedAtUtc.Offset);
        Assert.Equal(1, await restarted.Decisions.CountAsync(value => value.OrderId == request.OrderId));
    }

    [Fact]
    public async Task Replay_does_not_evaluate_or_validate_a_new_processing_timestamp()
    {
        var request = Request();
        await using var context = database.Context();
        var store = new PaymentDecisionStore(context);
        await store.GetOrCreateAsync(request, new(), ProcessedAt);
        var replay = await store.GetOrCreateAsync(request, new(700m), ProcessedAt.AddTicks(1));
        Assert.False(replay.Created);
        Assert.Equal(PaymentSimulationStatus.Rejected, replay.Decision.Status);
        Assert.Equal(ProcessedAt, replay.Decision.ProcessedAtUtc);
        Assert.Equal(500m, replay.Decision.ApprovalLimit);
    }

    [Fact]
    public async Task Approved_400_under_500_is_persisted_without_reason()
    {
        var request = Request(400m);
        await using (var writer = database.Context())
            await new PaymentDecisionStore(writer).GetOrCreateAsync(request, new(), ProcessedAt);
        await using var reader = database.Context();
        var saved = await reader.Decisions.SingleAsync(value => value.OrderId == request.OrderId);
        Assert.Equal(PaymentSimulationStatus.Approved, saved.Status);
        Assert.Null(saved.Reason);
        Assert.Equal(400m, saved.Price);
        Assert.Equal(ProcessedAt, saved.ProcessedAtUtc);
    }

    [Theory]
    [InlineData("UserId")]
    [InlineData("GameId")]
    [InlineData("Price")]
    [InlineData("PlacedAtUtc")]
    public async Task Divergent_field_conflicts_without_replacing_original(string field)
    {
        var request = Request();
        await using var context = database.Context();
        var store = new PaymentDecisionStore(context);
        await store.GetOrCreateAsync(request, new(), ProcessedAt);
        var changed = field switch
        {
            "UserId" => request with { UserId = Guid.NewGuid() },
            "GameId" => request with { GameId = Guid.NewGuid() },
            "Price" => request with { Price = 400m },
            _ => request with { PlacedAtUtc = PlacedAt.AddTicks(10) }
        };
        var error = await Assert.ThrowsAsync<PaymentDecisionConflictException>(() => store.GetOrCreateAsync(changed, new(700m), ProcessedAt.AddDays(1)));
        Assert.Equal(request.OrderId, error.OrderId);
        Assert.Equal(new[] { field }, error.DifferingFields);
        await using var reader = database.Context();
        var original = await reader.Decisions.SingleAsync(value => value.OrderId == request.OrderId);
        Assert.Equal(request.UserId, original.UserId);
        Assert.Equal(request.GameId, original.GameId);
        Assert.Equal(600m, original.Price);
        Assert.Equal(PlacedAt, original.PlacedAtUtc);
        Assert.Equal(ProcessedAt, original.ProcessedAtUtc);
        Assert.Equal(500m, original.ApprovalLimit);
    }

    [Fact]
    public async Task Different_order_id_creates_an_independent_decision()
    {
        var request = Request();
        await using var context = database.Context();
        var store = new PaymentDecisionStore(context);
        await store.GetOrCreateAsync(request, new(), ProcessedAt);
        var other = await store.GetOrCreateAsync(request with { OrderId = Guid.NewGuid() }, new(700m), ProcessedAt.AddTicks(10));
        Assert.True(other.Created);
        Assert.Equal(PaymentSimulationStatus.Approved, other.Decision.Status);
        Assert.Equal(2, await context.Decisions.CountAsync(value => value.OrderId == request.OrderId || value.OrderId == other.Decision.OrderId));
    }

    [Theory]
    [InlineData("OrderId")]
    [InlineData("UserId")]
    [InlineData("GameId")]
    [InlineData("NegativePrice")]
    [InlineData("ExcessPrice")]
    [InlineData("ThirdDecimal")]
    [InlineData("PlacedOffset")]
    [InlineData("PlacedTick")]
    [InlineData("ProcessedOffset")]
    [InlineData("ProcessedTick")]
    public async Task Invalid_input_never_persists_a_decision(string invalid)
    {
        var request = Request();
        var processed = ProcessedAt;
        request = invalid switch
        {
            "OrderId" => request with { OrderId = Guid.Empty },
            "UserId" => request with { UserId = Guid.Empty },
            "GameId" => request with { GameId = Guid.Empty },
            "NegativePrice" => request with { Price = -0.01m },
            "ExcessPrice" => request with { Price = 10000000000000000m },
            "ThirdDecimal" => request with { Price = 0.001m },
            "PlacedOffset" => request with { PlacedAtUtc = PlacedAt.ToOffset(TimeSpan.FromHours(-3)) },
            "PlacedTick" => request with { PlacedAtUtc = PlacedAt.AddTicks(1) },
            _ => request
        };
        if (invalid == "ProcessedOffset") processed = ProcessedAt.ToOffset(TimeSpan.FromHours(-3));
        if (invalid == "ProcessedTick") processed = ProcessedAt.AddTicks(1);
        await using var context = database.Context();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new PaymentDecisionStore(context).GetOrCreateAsync(request, new(), processed));
        Assert.False(await context.Decisions.AnyAsync(value => value.OrderId == request.OrderId));
    }

    [Fact]
    public async Task Replay_validates_offset_before_lookup_even_when_DateTimeOffset_equals_original_instant()
    {
        var request = Request();
        await using var context = database.Context();
        var store = new PaymentDecisionStore(context);
        await store.GetOrCreateAsync(request, new(), ProcessedAt);
        var sameInstant = request with { PlacedAtUtc = PlacedAt.ToOffset(TimeSpan.FromHours(-3)) };
        Assert.Equal(request.PlacedAtUtc, sameInstant.PlacedAtUtc);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.GetOrCreateAsync(sameInstant, new(700m), ProcessedAt));
        Assert.Equal("PlacedAtUtc", error.ParamName);
        Assert.Equal(1, await context.Decisions.CountAsync(value => value.OrderId == request.OrderId));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9999999999999999.99")]
    public async Task Monetary_boundaries_round_trip_exactly(string text)
    {
        var price = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var request = Request(price);
        var delay = TimeSpan.FromTicks(1234567);
        await using (var context = database.Context())
            await new PaymentDecisionStore(context).GetOrCreateAsync(request, new(price, delay), ProcessedAt);
        await using var reader = database.Context();
        var saved = await reader.Decisions.SingleAsync(value => value.OrderId == request.OrderId);
        Assert.Equal(price, saved.Price);
        Assert.Equal(price, saved.ApprovalLimit);
        Assert.Equal(1234567L, saved.DelayTicks);
        Assert.Equal(PaymentSimulationStatus.Approved, saved.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_transaction_controls_decision_and_associated_marker_atomically(bool commit)
    {
        var request = Request();
        var marker = Guid.NewGuid();
        await using (var context = database.Context())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var write = await new PaymentDecisionStore(context).GetOrCreateAsync(request, new(), ProcessedAt);
            Assert.True(write.Created);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO pay02_transaction_markers(id) VALUES ({marker})");
            await using (var observer = database.Context())
                Assert.False(await observer.Decisions.AnyAsync(value => value.OrderId == request.OrderId));
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
        }
        await using var reader = database.Context();
        Assert.Equal(commit, await reader.Decisions.AnyAsync(value => value.OrderId == request.OrderId));
        await using var connection = await database.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT count(*) FROM pay02_transaction_markers WHERE id = @id", connection);
        check.Parameters.AddWithValue("id", marker);
        Assert.Equal(commit ? 1L : 0L, await check.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Unsupported_isolation_is_rejected_without_writing(IsolationLevel isolation)
    {
        var request = Request();
        await using var context = database.Context();
        await using var transaction = await context.Database.BeginTransactionAsync(isolation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PaymentDecisionStore(context).GetOrCreateAsync(request, new(), ProcessedAt));
        Assert.False(await context.Decisions.AnyAsync(value => value.OrderId == request.OrderId));
    }

    [Fact]
    public async Task Foreign_database_and_superuser_privileges_are_unavailable_to_application_role()
    {
        await using var connection = await database.OpenAsync();
        await using var role = new NpgsqlCommand("SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_roles WHERE rolname = current_user", connection);
        Assert.Equal(true, await role.ExecuteScalarAsync());
        var foreignString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "foreign_database" }.ConnectionString;
        await using var foreign = new NpgsqlConnection(foreignString);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => foreign.OpenAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }
}
