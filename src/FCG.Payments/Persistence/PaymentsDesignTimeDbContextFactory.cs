using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FCG.Payments.Persistence;

/// <summary>Offline model scaffolding; database update commands must supply --connection explicitly.</summary>
public sealed class PaymentsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql("Host=localhost;Database=fcg_payments")
            .Options);
}
