using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace FCG.Payments.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class PaymentMigrationTests(PostgresFixture database, ITestOutputHelper output)
{
    [Fact]
    public async Task Empty_database_has_payment_decision_schema_after_application_migrations()
    {
        output.WriteLine($"PAY02 resource: container={database.ResourceName} id={database.ContainerId} volume={database.VolumeName}");
        await using var context = database.Context();
        await context.GetService<IMigrator>().MigrateAsync("0");
        await using var connection = await database.OpenAsync();
        await using (var absent = new NpgsqlCommand("SELECT to_regclass('public.payment_decisions')::text", connection))
            Assert.Equal(DBNull.Value, await absent.ExecuteScalarAsync());
        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.payment_decisions')::text", connection);
        Assert.Equal("payment_decisions", await command.ExecuteScalarAsync());
    }
}
