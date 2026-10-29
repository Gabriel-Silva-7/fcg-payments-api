using System.Diagnostics;
using Xunit;
namespace FCG.Payments.Tests.Recovery;
public sealed class RecoveryCliArgumentTests
{
    [Fact]
    public async Task Invalid_arguments_exit_safely_before_loading_configuration_or_starting_host()
    {
        var result = await Run(null, "recover-orders", "PRIVATE-INVALID-ARGUMENT");
        Assert.Equal(2, result.ExitCode); Assert.Contains("Usage:", result.Error);
        Assert.DoesNotContain("PRIVATE-INVALID-ARGUMENT", result.Output + result.Error); Assert.DoesNotContain("   at ", result.Error);
    }
    internal static Task<(int ExitCode, string Output, string Error)> Run(string? connection, params string[] args) => RunConfigured(connection, null, args);

    internal static async Task<(int ExitCode, string Output, string Error)> RunConfigured(string? connection, IReadOnlyDictionary<string, string?>? extra, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("Jwt__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("RabbitMq__", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        if (connection is not null) start.Environment["ConnectionStrings__Default"] = connection;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"; start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Database__MigrateOnStartup"] = "true";
        start.Environment["RabbitMq__Host"] = "127.0.0.1"; start.Environment["RabbitMq__Port"] = "1";
        if (extra is not null) foreach (var pair in extra) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); return (process.ExitCode, await output, await error); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
