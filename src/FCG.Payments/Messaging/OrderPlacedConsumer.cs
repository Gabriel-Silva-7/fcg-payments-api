using FCG.Contracts;
using MassTransit;

namespace FCG.Payments.Messaging;

public sealed class OrderPlacedConsumer(OrderProcessingHandler handler) : IConsumer<OrderPlacedEvent>
{
    public async Task Consume(ConsumeContext<OrderPlacedEvent> context) =>
        await handler.HandleAsync(context.Message, context.MessageId, context, context.CancellationToken);
}
