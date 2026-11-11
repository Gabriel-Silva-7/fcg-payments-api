using System.Text.Json;
using FCG.Contracts;
using FCG.Payments.Persistence;
using FCG.Payments.Processing;
using FCG.Payments.Tests.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FCG.Payments.Tests.Recovery;

[Collection("payments")]
public sealed class PaymentRecoveryTests(PaymentFixture fixture)
{
    [Fact]
    public async Task Restoring_original_winner_allows_one_atomic_replay_under_concurrent_operators()
    {
        await using var app = await fixture.CreateAsync(ResultIntegrityTests.DisableDelivery);
        app.Overrides["PaymentSimulation:ApprovalLimit"] = "0";
        using var client = app.CreateClient(); var order = ResultIntegrityTests.Order();
        await ResultIntegrityTests.Process(app, order);
        // Explicit owner restore fault: incoming message stays unchanged; only the backed-up winner is restored later.
        await Sql(app, "UPDATE payment_decisions SET price=price+1");
        await ResultIntegrityTests.Process(app, order);
        Guid id; string payload;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            id = (await db.MessageConflicts.SingleAsync()).Id; payload = (await db.Results.SingleAsync()).Payload;
        }
        Assert.Equal("DivergentOrderRequest", (await Invoke(app, id, false)).Reason);
        Assert.Equal("Quarantined", (await Invoke(app, id, true)).Outcome);
        await Sql(app, "UPDATE payment_decisions SET price=price-1");
        var before = await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\"");
        Assert.Equal("ReadyToStage", (await Invoke(app, id, false)).Outcome);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
        var outcomes = await Task.WhenAll(Invoke(app, id, true), Invoke(app, id, true));
        Assert.Single(outcomes, x => x.Outcome == "StagedOriginalResult");
        Assert.Single(outcomes, x => x.Outcome == "AlreadyResolved");
        Assert.Equal((long)before! + 1, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(3L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
        using var verification = app.Services.CreateScope(); var stored = verification.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Equal(payload, (await stored.Results.SingleAsync()).Payload);
        Assert.True((await stored.MessageConflicts.SingleAsync()).Resolved);
        Assert.Equal("Rejected", JsonSerializer.Deserialize<PaymentProcessedEvent>(payload)!.Status);
    }

    [Fact]
    public async Task Missing_decision_is_quarantined_and_preview_is_read_only()
    {
        await using var app = await fixture.CreateAsync(ResultIntegrityTests.DisableDelivery); using var client = app.CreateClient();
        var order = ResultIntegrityTests.Order(); await ResultIntegrityTests.Process(app, order);
        await ResultIntegrityTests.Process(app, new OrderPlacedEvent(order.OrderId, order.UserId, order.GameId, order.Price + 1, order.PlacedAtUtc));
        Guid id;
        using (var scope = app.Services.CreateScope()) id = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().MessageConflicts.Select(x => x.Id).SingleAsync();
        await Sql(app, "DELETE FROM \"Results\"; DELETE FROM payment_decisions");
        Assert.Equal("PaymentDecisionNotFound", (await Invoke(app, id, false)).Reason);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
        Assert.Equal("PaymentDecisionNotFound", (await Invoke(app, id, true)).Reason);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(false, await app.ScalarAsync("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        var cli = await RecoveryCliArgumentTests.Run(app.ConnectionString, "recover-orders", "replay", "--id", id.ToString());
        Assert.Equal(4, cli.ExitCode); Assert.Empty(cli.Error); Assert.Contains("PaymentDecisionNotFound", cli.Output);
        Assert.DoesNotContain("payload\"", cli.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
    }

    [Fact]
    public async Task Restored_result_inconsistent_with_original_decision_cannot_be_staged_by_operator()
    {
        await using var app = await fixture.CreateAsync(ResultIntegrityTests.DisableDelivery); using var client = app.CreateClient();
        var order = ResultIntegrityTests.Order(); await ResultIntegrityTests.Process(app, order);
        await Sql(app, "UPDATE payment_decisions SET price=price+1"); await ResultIntegrityTests.Process(app, order);
        await Sql(app, "UPDATE payment_decisions SET price=price-1");
        Guid id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(); id = (await db.MessageConflicts.SingleAsync()).Id;
            (await db.Results.SingleAsync()).Payload = "{}"; await db.SaveChangesAsync();
        }
        var before = await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\"");
        Assert.Equal("PersistedResultDoesNotMatchDecision", (await Invoke(app, id, false)).Reason);
        Assert.Equal("Quarantined", (await Invoke(app, id, true)).Outcome);
        Assert.Equal(before, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Cli_apply_stages_original_approved_result_under_zero_limit_without_starting_bus_or_http()
    {
        await using var app = await fixture.CreateAsync(ResultIntegrityTests.DisableDelivery); using var client = app.CreateClient();
        var order = ResultIntegrityTests.Order(); await ResultIntegrityTests.Process(app, order);
        await Sql(app, "UPDATE payment_decisions SET price=price+1"); await ResultIntegrityTests.Process(app, order);
        await Sql(app, "UPDATE payment_decisions SET price=price-1");
        Guid id; string original;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(); id = (await db.MessageConflicts.SingleAsync()).Id;
            original = (await db.Results.SingleAsync()).Payload;
        }
        // The application does not need an HTTP port or a reachable broker to commit the durable outbox.
        using var occupied = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); occupied.Start();
        var extra = fixture.BrokerSettings.ToDictionary(x => x.Key.Replace(":", "__", StringComparison.Ordinal), x => x.Value);
        extra["PaymentSimulation__ApprovalLimit"] = "0"; extra["PaymentSimulation__Delay"] = "00:00:00";
        extra["ASPNETCORE_URLS"] = "http://127.0.0.1:" + ((System.Net.IPEndPoint)occupied.LocalEndpoint).Port;
        var before = await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\"");
        var cli = await RecoveryCliArgumentTests.RunConfigured(app.ConnectionString, extra, "recover-orders", "replay", "--id", id.ToString(), "--apply");
        Assert.Equal(0, cli.ExitCode); Assert.Empty(cli.Error); Assert.Contains("StagedOriginalResult", cli.Output);
        Assert.Equal((long)before! + 1, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        using var verification = app.Services.CreateScope(); var stored = verification.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Equal(original, (await stored.Results.SingleAsync()).Payload);
        Assert.Equal("Approved", JsonSerializer.Deserialize<PaymentProcessedEvent>(original)!.Status);
        Assert.Equal(500m, await app.ScalarAsync("SELECT approval_limit FROM payment_decisions"));
    }

    [Fact]
    public async Task Missing_result_is_rebuilt_from_winner_and_failure_to_audit_rolls_back_every_effect()
    {
        await using var app = await fixture.CreateAsync(ResultIntegrityTests.DisableDelivery); using var client = app.CreateClient();
        var order = ResultIntegrityTests.Order(); await ResultIntegrityTests.Process(app, order);
        await Sql(app, "UPDATE payment_decisions SET price=price+1"); await ResultIntegrityTests.Process(app, order);
        await Sql(app, "UPDATE payment_decisions SET price=price-1; DELETE FROM \"Results\"");
        Guid id;
        using (var scope = app.Services.CreateScope()) id = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().MessageConflicts.Select(x => x.Id).SingleAsync();
        var preview = await Invoke(app, id, false); Assert.Equal("ReadyToStage", preview.Outcome);
        Assert.Contains("RebuildResultFromOriginalDecision", preview.PlannedEffects!);
        var before = await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\"");
        await Sql(app, "CREATE FUNCTION fail_recovery_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'controlled audit failure'; END $$; CREATE TRIGGER fail_audit BEFORE INSERT ON \"PaymentRecoveryAttempts\" FOR EACH ROW EXECUTE FUNCTION fail_recovery_audit()");
        await Assert.ThrowsAsync<DbUpdateException>(() => Invoke(app, id, true));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Results\""));
        Assert.Equal(before, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(false, await app.ScalarAsync("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        await Sql(app, "DROP TRIGGER fail_audit ON \"PaymentRecoveryAttempts\"; DROP FUNCTION fail_recovery_audit()");
        Assert.Equal("StagedOriginalResult", (await Invoke(app, id, true)).Outcome);
        using var check = app.Services.CreateScope(); var db = check.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var decision = await db.Decisions.SingleAsync(); var result = JsonSerializer.Deserialize<PaymentProcessedEvent>((await db.Results.SingleAsync()).Payload)!;
        Assert.Equal(decision.ProcessedAtUtc, result.ProcessedAtUtc); Assert.Equal(decision.Price, result.Price);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
    }

    private static async Task<PaymentRecoveryResult> Invoke(PaymentFactory app, Guid id, bool apply)
    {
        using var scope = app.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<PaymentRecoveryService>();
        return apply ? await service.ReplayAsync(id, CancellationToken.None) : await service.PreviewAsync(id, CancellationToken.None);
    }
    private static async Task Sql(PaymentFactory app, string sql)
    {
        using var scope = app.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.ExecuteSqlRawAsync(sql);
    }
}
