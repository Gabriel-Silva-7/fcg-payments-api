using System.Data;
using FCG.Contracts;
using FCG.Payments.Persistence;
using MassTransit;

namespace FCG.Payments.Messaging;

public static class PaymentsMessaging
{
    public static IServiceCollection AddPaymentsMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new(); settings.Validate();
        var queue = configuration["Queues:Orders"] ?? "fcg-payments-orders";
        if (string.IsNullOrWhiteSpace(queue) || queue.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidOperationException("Queues:Orders requires a valid durable endpoint name.");
        services.AddSingleton(settings);
        services.AddScoped<PaymentDecisionStore>();
        services.AddScoped<OrderProcessingHandler>();
        services.AddMassTransit(bus =>
        {
            bus.ConfigureHealthCheckOptions(options => { options.Tags.Clear(); options.Tags.Add("diagnostic"); });
            bus.AddConsumer<OrderPlacedConsumer>();
            bus.AddEntityFrameworkOutbox<PaymentsDbContext>(outbox =>
            {
                outbox.UsePostgres(); outbox.IsolationLevel = IsolationLevel.ReadCommitted;
                outbox.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
                outbox.UseBusOutbox(options => options.MessageDeliveryTimeout = TimeSpan.FromSeconds(5));
            });
            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(settings.Host, settings.Port, settings.VirtualHost, host =>
                { host.Username(settings.Username); host.Password(settings.Password); host.PublisherConfirmation = true; });
                rabbit.ConfigureJsonSerializerOptions(options =>
                {
                    options.Converters.Insert(0, new OrderPlacedEventConverter());
                    options.Converters.Insert(0, new PaymentProcessedEventConverter());
                    return options;
                });
                rabbit.ReceiveEndpoint(queue, endpoint =>
                {
                    endpoint.UseMessageRetry(retry =>
                    {
                        retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                        retry.Ignore<ArgumentException>();
                    });
                    endpoint.UseEntityFrameworkOutbox<PaymentsDbContext>(context);
                    endpoint.ConfigureConsumer<OrderPlacedConsumer>(context);
                });
            });
        });
        services.Configure<MassTransitHostOptions>(options => { options.WaitUntilStarted = false; options.StopTimeout = TimeSpan.FromSeconds(5); });
        services.AddHealthChecks().AddCheck<PaymentTransportHealthCheck>("payments-transport", tags: ["ready"]);
        return services;
    }
}
