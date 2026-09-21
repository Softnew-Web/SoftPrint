using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Auth;
using SoftPrint.Infrastructure.Configuration;
using SoftPrint.Infrastructure.Persistence;
using SoftPrint.Printing.Linux;
using Xunit;

namespace SoftPrint.Tests;

[Collection("Environment")]
public sealed class PlatformTests
{
    [Fact]
    public async Task SoftPrintApiHeader_IsAccepted()
    {
        var called = false;
        var middleware = new ApiKeyMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            new TestKeyProvider());
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SoftPrint-Key"] = TestKeyProvider.Key;

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task LegacyApiHeader_RemainsAccepted()
    {
        var called = false;
        var middleware = new ApiKeyMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            new TestKeyProvider());
        var context = new DefaultHttpContext();
        context.Request.Headers["X-AutoPrint-Key"] = TestKeyProvider.Key;

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public void LinuxMetrics_ReturnPortableFallback()
    {
        var metrics = new LinuxPrinterPageMetrics().Read("", new PrintOptions());
        Assert.Equal("cups-approximate", metrics.Source);
        Assert.Equal(210, metrics.PageWidthMm);
        Assert.Equal(297, metrics.PageHeightMm);
    }

    [Fact]
    public void CupsCatalog_DoesNotCrashWhenCupsIsMissing()
    {
        var exception = Record.Exception(() => new CupsPrinterCatalog().ListDetailed());
        Assert.Null(exception);
    }

    [Fact]
    public void CupsCatalog_ParsesLpstatOutput()
    {
        var runner = new FakeCommandRunner();
        runner.Responses["lpstat -a"] = "Office_Printer accepting requests since forever\nKitchen accepting requests\n";
        runner.Responses["lpstat -d"] = "system default destination: Office_Printer\n";
        runner.Responses["lpoptions -p Office_Printer"] = "device-uri=ipp://printer printer-state=3\n";
        runner.Responses["lpoptions -p Kitchen"] = "device-uri=usb://printer\n";

        var printers = new CupsPrinterCatalog(runner).ListDetailed();

        Assert.Equal(2, printers.Count);
        Assert.True(printers.Single(p => p.Name == "Office_Printer").IsDefault);
        Assert.Equal("USB", printers.Single(p => p.Name == "Kitchen").Connection);
    }

    [Fact]
    public void TcpPrinterTarget_AcceptsExplicitJetDirectAddresses()
    {
        Assert.True(TcpPrinterTarget.TryParse("tcp:192.168.1.20:9100", out var host, out var port));
        Assert.Equal("192.168.1.20", host);
        Assert.Equal(9100, port);
        Assert.False(TcpPrinterTarget.TryParse("Office_Printer", out _, out _));
    }

    [Fact]
    public void SystemSettings_AreClampedAndSecretIsStoredOutsideEnv()
    {
        var root = Path.Combine(Path.GetTempPath(), $"softprint-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var repository = new JsonSystemSettingsRepository(
                new TestPaths(root),
                Options.Create(new SoftPrintFeatureOptions()));
            var saved = repository.Update(new SystemSettings(
                1, 0, 1, true, true, "", "secret",
                1, 0, 500, 0, 1, true, " https://telemetry.example/hook "));

            Assert.Equal(100, saved.PollIntervalMs);
            Assert.Equal(1, saved.RetentionDays);
            Assert.True(saved.TelemetryEnabled);
            Assert.Equal("https://telemetry.example/hook", saved.TelemetryUrl);
            Assert.True(File.Exists(Path.Combine(root, "system-settings.json")));
            Assert.Contains("secret", File.ReadAllText(Path.Combine(root, "system-settings.json")), StringComparison.Ordinal);
            Assert.Contains("telemetry", File.ReadAllText(Path.Combine(root, "system-settings.json")), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EnvFile_IsOptionalAndDoesNotCreateDotEnv()
    {
        var root = Path.Combine(Path.GetTempPath(), $"softprint-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".env.example"), "SIMULATION=false");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddSoftPrintEnvFile(root)
                .Build();
            Assert.False(File.Exists(Path.Combine(root, ".env")));
            Assert.Null(configuration["SoftPrint:Simulation"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LegacyAutoPrintSection_IsMappedWhenSoftPrintIsMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AutoPrint:WebhookUrl"] = "http://localhost/hook"
            })
            .AddLegacyAutoPrintAliases()
            .Build();

        Assert.Equal("http://localhost/hook", configuration["SoftPrint:WebhookUrl"]);
    }

    [Fact]
    public void LegacyData_IsMigratedToPerUserDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"softprint-migration-{Guid.NewGuid():N}");
        var legacy = Path.Combine(root, "legacy");
        var target = Path.Combine(root, "user-data");
        var config = Path.Combine(root, "user-config");
        Directory.CreateDirectory(Path.Combine(legacy, "data"));
        File.WriteAllText(Path.Combine(legacy, "data", "jobs.json"), "[]");
        var previousData = Environment.GetEnvironmentVariable("SOFTPRINT_DATA_ROOT");
        var previousConfig = Environment.GetEnvironmentVariable("SOFTPRINT_CONFIG_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("SOFTPRINT_DATA_ROOT", target);
            Environment.SetEnvironmentVariable("SOFTPRINT_CONFIG_ROOT", config);
            _ = new UserAppPaths(new TestHostEnvironment(legacy));
            Assert.True(File.Exists(Path.Combine(target, "jobs.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOFTPRINT_DATA_ROOT", previousData);
            Environment.SetEnvironmentVariable("SOFTPRINT_CONFIG_ROOT", previousConfig);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FakeCommandRunner : IExternalCommandRunner
    {
        public Dictionary<string, string> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Capture(string fileName, params string[] args) =>
            Responses.TryGetValue(fileName + " " + string.Join(" ", args), out var value) ? value : "";
        public int Run(string fileName, params string[] args) => 0;
        public Task<int> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class TestKeyProvider : IApiKeyProvider
    {
        public const string Key = "12345678901234567890123456789012";
        public string ApiKey => Key;
    }

    private sealed record TestPaths(string Root) : IAppPaths
    {
        public string DataRoot => Root;
        public string ConfigRoot => Root;
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SoftPrint.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;
