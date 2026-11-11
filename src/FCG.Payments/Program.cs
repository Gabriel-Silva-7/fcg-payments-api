using FCG.Payments;
using FCG.Payments.Persistence;
using FCG.Payments.Messaging;
using FCG.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

if (args.FirstOrDefault() == "recover-orders")
{
    Environment.ExitCode = await FCG.Payments.Operations.PaymentRecoveryCommand.RunAsync(args[1..]);
    return;
}
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    Console.Error.WriteLine("Usage: recover-orders list | show --id GUID | replay --id GUID [--dry-run | --apply]");
    Environment.ExitCode = 2;
    return;
}
if (args is ["--healthcheck"])
{
    Environment.ExitCode = await ContainerProbe.RunAsync(new Uri("http://127.0.0.1:8080/health/ready"));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Default is required for Payments.");
builder.Services.AddDbContext<PaymentsDbContext>(options =>
{
    options.UseNpgsql(connection);
    if (!builder.Environment.IsDevelopment())
        options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.CommandError, RelationalEventId.ConnectionError,
            CoreEventId.QueryIterationFailed, CoreEventId.SaveChangesFailed));
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddOptions<PaymentSimulationOptions>().Bind(builder.Configuration.GetSection(PaymentSimulationOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PaymentSimulationOptions>, PaymentSimulationOptionsValidator>();
builder.Services.AddPaymentsMessaging(builder.Configuration);
builder.Services.AddScoped<FCG.Payments.Processing.PaymentRecoveryService>();
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddFilter("MassTransit", LogLevel.None);
    // O framework inclui caminho/query brutos nos logs HTTP; podem conter dados sensíveis.
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
}
builder.Services.AddHealthChecks().AddCheck<PaymentsStorageHealthCheck>("payments-storage", tags: ["ready"]);
var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});
app.Run();

public partial class Program;
