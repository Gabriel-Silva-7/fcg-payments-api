using Npgsql;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Volumes;
using FCG.Payments.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace FCG.Payments.Tests.Persistence;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "payments-postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string appPassword = Guid.NewGuid().ToString("N");
    private readonly IVolume volume;
    private readonly PostgreSqlContainer container;
    public string ResourceName { get; } = "fcg-pay02-" + Guid.NewGuid().ToString("N");
    public string ContainerId => container.Id;
    public string VolumeName => volume.Name;

    public PostgresFixture()
    {
        volume = new VolumeBuilder().WithName(ResourceName + "-data").WithLabel("fcg.task", "F2-PAY-02").Build();
        container = new PostgreSqlBuilder()
            .WithImage("postgres:16@sha256:f1c3376c26f2609ab9f29f71f824103fe2fcd8ee0346485cb6122a4f93df6f94")
            .WithName(ResourceName)
            .WithLabel("fcg.task", "F2-PAY-02")
            .WithVolumeMount(volume, "/var/lib/postgresql/data")
            .WithUsername("postgres")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();
    }

    public string ConnectionString => new NpgsqlConnectionStringBuilder(container.GetConnectionString())
    {
        Username = "fcg_payments", Password = appPassword, Database = "fcg_payments",
        IncludeErrorDetail = false, Pooling = false
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        await volume.CreateAsync();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        // Password contains only generated hex. Identifiers are constants, never user input.
        await using var role = new NpgsqlCommand($"CREATE ROLE fcg_payments LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{appPassword}'", admin);
        await role.ExecuteNonQueryAsync();
        foreach (var sql in new[]
        {
            "CREATE DATABASE fcg_payments OWNER fcg_payments",
            "REVOKE CONNECT ON DATABASE fcg_payments FROM PUBLIC",
            "CREATE DATABASE foreign_database",
            "REVOKE CONNECT ON DATABASE foreign_database FROM PUBLIC"
        })
        {
            await using var command = new NpgsqlCommand(sql, admin);
            await command.ExecuteNonQueryAsync();
        }
        await using var context = Context();
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE pay02_transaction_markers (id uuid PRIMARY KEY)");
    }

    public PaymentsDbContext Context() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql(ConnectionString).Options);

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync()
    {
        try { await container.DisposeAsync(); }
        finally { await volume.DeleteAsync(); }
    }
}
