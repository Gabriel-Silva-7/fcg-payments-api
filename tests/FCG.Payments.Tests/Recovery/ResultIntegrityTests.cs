using System.Text.Json;
using FCG.Contracts;
using FCG.Payments.Messaging;
using FCG.Payments.Persistence;
using FCG.Payments.Tests.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FCG.Payments.Tests.Recovery;

[Collection("payments")]
public sealed class ResultIntegrityTests(PaymentFixture fixture)
{
    [Fact]
    public async Task Incompatible_restored_result_cannot_be_published_as_the_original_decision()
    {
        await using var app = await fixture.CreateAsync(DisableDelivery); using var client = app.CreateClient(); var order = Order();
        await Process(app, order);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(); var result = await db.Results.SingleAsync();
            var original = JsonSerializer.Deserialize<PaymentProcessedEvent>(result.Payload)!;
            result.Payload = JsonSerializer.Serialize(new PaymentProcessedEvent(original.OrderId, original.UserId, original.GameId,
                original.Price + 1m, original.Status, original.Reason, original.ProcessedAtUtc));
            await db.SaveChangesAsync();
        }
        var before = await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\"");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Process(app, order));
        Assert.Equal(before, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(order.Price, await app.ScalarAsync("SELECT price FROM payment_decisions"));
    }

    internal static OrderPlacedEvent Order(decimal price = 49.90m) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), price, DateTimeOffset.Parse("2026-10-28T14:00:00Z"));
    internal static async Task Process(PaymentFactory app, OrderPlacedEvent message)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await scope.ServiceProvider.GetRequiredService<OrderProcessingHandler>().HandleAsync(message, Guid.NewGuid(), scope.ServiceProvider.GetRequiredService<IPublishEndpoint>(), CancellationToken.None);
        await transaction.CommitAsync();
    }
    internal static void DisableDelivery(IServiceCollection services)
    {
        foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService)
            && x.ImplementationType?.Name.StartsWith("BusOutboxDeliveryService", StringComparison.Ordinal) == true).ToArray()) services.Remove(service);
    }
}
