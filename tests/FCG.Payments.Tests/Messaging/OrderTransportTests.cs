using FCG.Contracts;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FCG.Payments.Tests.Messaging;

[Collection("payments")]
public sealed class OrderTransportTests(PaymentFixture fixture)
{
    [Fact]
    public async Task Real_order_is_processed_and_result_is_persisted()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        using var scope = app.Services.CreateScope(); var order = new OrderPlacedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 49.90m,
            DateTimeOffset.Parse("2026-10-28T14:00:00Z"));
        await scope.ServiceProvider.GetRequiredService<IBus>().Publish(order);
        await Eventually(async () => Equals(1L, await app.ScalarAsync("SELECT count(*) FROM payment_decisions")));
    }
    internal static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition()) await Task.Delay(100, timeout.Token);
    }
}
