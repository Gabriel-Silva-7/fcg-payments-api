using System.Data.Common;
using System.Text;
using System.Text.Json;
using FCG.Contracts;
using FCG.Payments.Messaging;
using FCG.Payments.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit;
using static FCG.Payments.Tests.Messaging.OrderTransportTests;

namespace FCG.Payments.Tests.Messaging;

[Collection("payments")]
public sealed class OrderProcessingTests(PaymentFixture fixture)
{
    [Theory]
    [InlineData("0", "Approved")]
    [InlineData("500", "Approved")]
    [InlineData("500.01", "Rejected")]
    public async Task Real_transport_fans_out_canonical_result_to_independent_queues(string amount, string expected)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync();
        var queues = await ResultQueues(channel);
        var order = Order(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
        await app.Services.GetRequiredService<IBus>().Publish(order);
        var first = await Result(channel, queues[0]); var second = await Result(channel, queues[1]);
        Assert.Equal(first, second); Assert.Equal(order.OrderId, first.OrderId); Assert.Equal(order.UserId, first.UserId);
        Assert.Equal(order.GameId, first.GameId); Assert.Equal(order.Price, first.Price); Assert.Equal(expected, first.Status);
        Assert.Equal(expected == "Rejected" ? "AmountAboveLimit" : null, first.Reason);
        Assert.Equal(first, JsonSerializer.Deserialize<PaymentProcessedEvent>((string)(await app.ScalarAsync("SELECT \"Payload\" FROM \"Results\""))!));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
    }

    [Fact]
    public async Task New_message_id_after_restart_and_changed_limit_republishes_original_rejection()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync();
        var queues = await ResultQueues(channel); var order = Order(600m);
        await app.Services.GetRequiredService<IBus>().Publish(order);
        var original = await Result(channel, queues[0]); Assert.Equal("Rejected", original.Status);
        var payload = await app.ScalarAsync("SELECT \"Payload\" FROM \"Results\"");
        await app.DisposeAsync();
        await using var restarted = new PaymentFactory(app.ConnectionString, fixture.BrokerSettings);
        restarted.Overrides["PaymentSimulation:Delay"] = "00:00:00";
        restarted.Overrides["PaymentSimulation:ApprovalLimit"] = "700";
        // A new process has no AsyncLocal logger inherited from a disposed MassTransit host.
        Task<HttpClient> starting;
        using (ExecutionContext.SuppressFlow()) starting = Task.Run(() => restarted.CreateClient());
        using var restartedClient = await starting;
        await restarted.Services.GetRequiredService<IBus>().Publish(order, context => context.MessageId = Guid.NewGuid());
        Assert.Equal(original, await Result(channel, queues[0]));
        Assert.Equal(payload, await restarted.ScalarAsync("SELECT \"Payload\" FROM \"Results\""));
        Assert.Equal(1L, await restarted.ScalarAsync("SELECT count(*) FROM payment_decisions"));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("game")]
    [InlineData("price")]
    [InlineData("time")]
    public async Task Divergent_order_preserves_decision_result_and_durable_diagnostic(string field)
    {
        await using var app = await fixture.CreateAsync(DisableDelivery); using var client = app.CreateClient(); var order = Order();
        await Process(app, order);
        var original = await app.ScalarAsync("SELECT \"Payload\" FROM \"Results\"");
        var changed = new OrderPlacedEvent(order.OrderId, field == "user" ? Guid.NewGuid() : order.UserId,
            field == "game" ? Guid.NewGuid() : order.GameId, field == "price" ? 60m : order.Price,
            field == "time" ? order.PlacedAtUtc.AddSeconds(1) : order.PlacedAtUtc);
        await Process(app, changed); await Process(app, changed);
        Assert.Equal(original, await app.ScalarAsync("SELECT \"Payload\" FROM \"Results\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(2, await app.ScalarAsync("SELECT \"Observations\" FROM \"MessageConflicts\""));
        Assert.Equal("OrderPlacedEvent", await app.ScalarAsync("SELECT \"MessageType\" FROM \"MessageConflicts\""));
    }

    [Fact]
    public async Task Saved_decision_result_and_outbox_all_roll_back_when_commit_fails_then_retry_commits()
    {
        var failure = new FailCommitOnce();
        await using var app = await fixture.CreateAsync(services =>
        { DisableDelivery(services); services.AddDbContext<PaymentsDbContext>(options => options.AddInterceptors(failure)); });
        using var client = app.CreateClient(); var order = Order(); failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Process(app, order)); Assert.True(failure.SawAtomicWrites);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Results\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        await Process(app, order);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Results\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Concurrent_local_transactions_keep_one_decision_and_publish_only_original_result()
    {
        await using var app = await fixture.CreateAsync(DisableDelivery); using var client = app.CreateClient(); var order = Order(600m);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Enumerable.Range(0, 6).Select(async _ => { await gate.Task; await Process(app, order); }).ToArray();
        gate.SetResult(); await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Results\""));
        Assert.Equal(6L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(DISTINCT \"Body\"::jsonb->'message') FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Database_unavailability_changes_readiness_not_liveness_and_logs_remain_private()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await fixture.PauseDatabaseAsync();
        try
        {
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
        finally { await fixture.UnpauseDatabaseAsync(); }
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Invalid_wire_order_is_routed_to_error_without_financial_rejection()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync();
        var error = app.OrdersQueue + "_error";
        await channel.QueueDeclareAsync(error, durable: true, exclusive: false, autoDelete: false);
        var payload = JsonSerializer.Serialize(Order()).Replace("49.90", "-1.00");
        var envelope = JsonSerializer.Serialize(new { messageId = Guid.NewGuid(), messageType = new[] { "urn:message:FCG.Contracts:OrderPlacedEvent" },
            message = JsonSerializer.Deserialize<JsonElement>(payload), headers = new { } });
        await channel.BasicPublishAsync("FCG.Contracts:OrderPlacedEvent", "", mandatory: true,
            basicProperties: new BasicProperties { ContentType = "application/vnd.masstransit+json" }, body: Encoding.UTF8.GetBytes(envelope));
        BasicGetResult? failed = null;
        await Eventually(async () => (failed = await channel.BasicGetAsync(error, autoAck: false)) is not null);
        await channel.BasicAckAsync(failed!.DeliveryTag, multiple: false);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Results\""));
    }

    [Fact]
    public async Task Exhausted_storage_failure_is_error_not_a_financial_rejection_and_redrive_recovers()
    {
        var failure = new FailDecisionSaves();
        await using var app = await fixture.CreateAsync(services => services.AddDbContext<PaymentsDbContext>(options => options.AddInterceptors(failure)));
        using var client = app.CreateClient();
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync();
        var error = app.OrdersQueue + "_error"; await channel.QueueDeclareAsync(error, durable: true, exclusive: false, autoDelete: false);
        var order = Order(); await app.Services.GetRequiredService<IBus>().Publish(order);
        BasicGetResult? failed = null;
        await Eventually(async () => (failed = await channel.BasicGetAsync(error, autoAck: false)) is not null);
        Assert.Equal(4, failure.Attempts);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Results\""));
        failure.Enabled = false;
        var properties = new BasicProperties(failed!.BasicProperties);
        await channel.BasicPublishAsync("", app.OrdersQueue, mandatory: true, basicProperties: properties, body: failed.Body);
        await channel.BasicAckAsync(failed.DeliveryTag, multiple: false);
        await Eventually(async () => Equals(1L, await app.ScalarAsync("SELECT count(*) FROM \"Results\"")));
        Assert.DoesNotContain("secret-storage-failure", string.Join('\n', app.Logs.Entries));
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Real_conflict_commits_diagnostic_and_ack_without_another_result()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var order = Order();
        await app.Services.GetRequiredService<IBus>().Publish(order);
        await Eventually(async () => Equals(1L, await app.ScalarAsync("SELECT count(*) FROM \"Results\"")));
        var conflicting = new OrderPlacedEvent(order.OrderId, order.UserId, order.GameId, 70m, order.PlacedAtUtc);
        var messageId = Guid.NewGuid();
        await app.Services.GetRequiredService<IBus>().Publish(conflicting, context => context.MessageId = messageId);
        await Eventually(async () => Equals(1L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\"")));
        await Eventually(async () => Equals(1L, await app.ScalarAsync($"SELECT count(*) FROM \"InboxState\" WHERE \"MessageId\" = '{messageId}' AND \"Delivered\" IS NOT NULL")));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions"));
        Assert.Equal(49.90m, await app.ScalarAsync("SELECT price FROM payment_decisions"));
    }

    [Fact]
    public async Task Broker_unavailable_still_commits_bus_outbox_and_restart_delivers_it()
    {
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync();
        var queues = await ResultQueues(channel);
        await using var app = await fixture.CreateAsync(services =>
            services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = false));
        app.Overrides["RabbitMq:Port"] = "1"; using var client = app.CreateClient();
        var order = Order(); await Process(app, order);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Results\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await app.DisposeAsync();
        await using var restarted = new PaymentFactory(app.ConnectionString, fixture.BrokerSettings);
        restarted.Overrides["PaymentSimulation:Delay"] = "00:00:00";
        Task<HttpClient> starting;
        using (ExecutionContext.SuppressFlow()) starting = Task.Run(() => restarted.CreateClient());
        using var restartedClient = await starting;
        var result = await Result(channel, queues[0]); Assert.Equal(order.OrderId, result.OrderId);
        Assert.Equal(result, await Result(channel, queues[1]));
        await Eventually(async () => Equals(0L, await restarted.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\"")));
    }

    [Fact]
    public async Task Failure_persisting_conflict_rolls_back_until_diagnostic_can_be_committed()
    {
        var failure = new FailConflictSaves();
        await using var app = await fixture.CreateAsync(services =>
        { DisableDelivery(services); services.AddDbContext<PaymentsDbContext>(options => options.AddInterceptors(failure)); });
        using var client = app.CreateClient(); var order = Order(); await Process(app, order);
        var divergent = new OrderPlacedEvent(order.OrderId, order.UserId, order.GameId, 90m, order.PlacedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Process(app, divergent));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        failure.Enabled = false; await Process(app, divergent);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(49.90m, await app.ScalarAsync("SELECT price FROM payment_decisions"));
    }

    private sealed class FailConflictSaves : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<MessageConflict>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Synthetic diagnostic failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailDecisionSaves : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<PersistedPaymentDecision>().Any(x => x.State == EntityState.Added))
            { Attempts++; throw new InvalidOperationException("secret-storage-failure"); }
            return ValueTask.FromResult(result);
        }
    }

    private static async Task Process(PaymentFactory app, OrderPlacedEvent message)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await scope.ServiceProvider.GetRequiredService<OrderProcessingHandler>().HandleAsync(message, Guid.NewGuid(),
            scope.ServiceProvider.GetRequiredService<IPublishEndpoint>(), CancellationToken.None);
        await transaction.CommitAsync();
    }
    private async Task<IConnection> Connect()
    {
        var settings = fixture.BrokerSettings;
        return await new ConnectionFactory { HostName = settings["RabbitMq:Host"]!, Port = int.Parse(settings["RabbitMq:Port"]!),
            VirtualHost = "/fcg", UserName = settings["RabbitMq:Username"]!, Password = settings["RabbitMq:Password"]! }.CreateConnectionAsync();
    }
    private static async Task<string[]> ResultQueues(IChannel channel)
    {
        await channel.ExchangeDeclareAsync("FCG.Contracts:PaymentProcessedEvent", "fanout", durable: true);
        var queues = new[] { "catalog-result-" + Guid.NewGuid().ToString("N"), "notification-result-" + Guid.NewGuid().ToString("N") };
        foreach (var queue in queues)
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(queue, "FCG.Contracts:PaymentProcessedEvent", "");
        }
        return queues;
    }
    private static async Task<PaymentProcessedEvent> Result(IChannel channel, string queue)
    {
        BasicGetResult? message = null;
        await Eventually(async () => (message = await channel.BasicGetAsync(queue, autoAck: false)) is not null);
        using var envelope = JsonDocument.Parse(message!.Body);
        var result = JsonSerializer.Deserialize<PaymentProcessedEvent>(envelope.RootElement.GetProperty("message"))!;
        Assert.Equal(result.OrderId, envelope.RootElement.GetProperty("correlationId").GetGuid());
        Assert.Contains("urn:message:FCG.Contracts:PaymentProcessedEvent", Encoding.UTF8.GetString(message.Body.Span));
        await channel.BasicAckAsync(message.DeliveryTag, multiple: false); return result;
    }
    private static OrderPlacedEvent Order(decimal price = 49.90m) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), price,
        DateTimeOffset.Parse("2026-10-28T14:00:00Z"));
    private static void DisableDelivery(IServiceCollection services)
    {
        foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService)
            && x.ImplementationType?.Name.StartsWith("BusOutboxDeliveryService", StringComparison.Ordinal) == true).ToArray()) services.Remove(service);
    }
    private sealed class FailCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool SawAtomicWrites { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var query = transaction.Connection!.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT (SELECT count(*) FROM payment_decisions) = 1 AND (SELECT count(*) FROM \"Results\") = 1 AND (SELECT count(*) FROM \"OutboxMessage\") = 1";
            SawAtomicWrites = (bool)(await query.ExecuteScalarAsync(cancellationToken))!;
            if (!SawAtomicWrites) return result;
            Armed = false;
            throw new InvalidOperationException("Synthetic pre-commit failure.");
        }
    }
}
