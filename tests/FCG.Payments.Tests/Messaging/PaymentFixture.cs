using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;
using MassTransit;

namespace FCG.Payments.Tests.Messaging;

[CollectionDefinition("payments", DisableParallelization = true)]
public sealed class PaymentCollection : ICollectionFixture<PaymentFixture>;

public sealed class PaymentFixture : IAsyncLifetime
{
    private readonly string _brokerPassword = Guid.NewGuid().ToString("N");
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16@sha256:f1c3376c26f2609ab9f29f71f824103fe2fcd8ee0346485cb6122a4f93df6f94").WithUsername("postgres").Build();
    public RabbitMqContainer Broker { get; }
    public PaymentFixture() => Broker = new RabbitMqBuilder()
        .WithImage("rabbitmq:4.3.6-management@sha256:54cb013d76cd1fd6c0a0708f34062ad61c452bb6f016b434c6c33f1a765d2b58")
        .WithUsername("fcg_payments").WithPassword(_brokerPassword).WithEnvironment("RABBITMQ_DEFAULT_VHOST", "/fcg").Build();
    public IReadOnlyDictionary<string, string?> BrokerSettings => new Dictionary<string, string?>
    {
        ["RabbitMq:Host"] = Broker.Hostname, ["RabbitMq:Port"] = Broker.GetMappedPublicPort(5672).ToString(),
        ["RabbitMq:VirtualHost"] = "/fcg", ["RabbitMq:Username"] = "fcg_payments", ["RabbitMq:Password"] = _brokerPassword
    };
    public async Task InitializeAsync() { await _database.StartAsync(); await Broker.StartAsync(); }
    public async Task<PaymentFactory> CreateAsync(Action<IServiceCollection>? customize = null)
    {
        var name = "pay_" + Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        await using var db = new NpgsqlConnection(_database.GetConnectionString()); await db.OpenAsync();
        foreach (var sql in new[] { $"CREATE ROLE \"{name}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE", $"CREATE DATABASE \"{name}\" OWNER \"{name}\"", $"REVOKE ALL ON DATABASE \"{name}\" FROM PUBLIC" })
        { await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(); }
        var connection = new NpgsqlConnectionStringBuilder(_database.GetConnectionString()) { Database = name, Username = name, Password = password, IncludeErrorDetail = false, Timeout = 2, CommandTimeout = 3 };
        return new PaymentFactory(connection.ConnectionString, BrokerSettings, customize);
    }
    public Task PauseDatabaseAsync() => _database.PauseAsync();
    public Task UnpauseDatabaseAsync() => _database.UnpauseAsync();
    public async Task DisposeAsync() { await Broker.DisposeAsync(); await _database.DisposeAsync(); }
}

public sealed class PaymentFactory(string connectionString, IReadOnlyDictionary<string, string?> brokerSettings,
    Action<IServiceCollection>? customize = null) : WebApplicationFactory<Program>
{
    public string ConnectionString { get; } = connectionString;
    public CaptureLogs Logs { get; } = new();
    public string OrdersQueue { get; } = "pay-orders-" + Guid.NewGuid().ToString("N");
    public Dictionary<string, string?> Overrides { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        var settings = new Dictionary<string, string?>(brokerSettings)
        { ["ConnectionStrings:Default"] = ConnectionString, ["Database:MigrateOnStartup"] = "true", ["Queues:Orders"] = OrdersQueue, ["PaymentSimulation:Delay"] = "00:00:00" };
        foreach (var pair in Overrides) settings[pair.Key] = pair.Value;
        foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
        builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.AddProvider(Logs));
            services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
            customize?.Invoke(services);
        });
    }
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var db = new NpgsqlConnection(ConnectionString); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); return await command.ExecuteScalarAsync();
    }
}

public sealed class CaptureLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(Entries, categoryName);
    public void Dispose() { }
    private sealed class Logger(ConcurrentQueue<string> entries, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(category + " " + formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
