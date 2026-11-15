using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Operations;
using SoftPrint.Infrastructure.Persistence;
using SoftPrint.Infrastructure.Updates;
using Xunit;

namespace SoftPrint.Tests;

/// <summary>
/// <c>SoftPrint.exe --config-root X --headless</c> não pode ler nem gravar nada fora de X —
/// nem o %LocalAppData% real (update, WebView2, AutoPrint) nem o %TEMP%.
/// </summary>
[Collection("Environment")]
public sealed class StateIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softprint-isolation-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousConfig = Environment.GetEnvironmentVariable(SoftPrintStateLocation.ConfigRootVariable);
    private readonly string? _previousData = Environment.GetEnvironmentVariable(SoftPrintStateLocation.DataRootVariable);

    public StateIsolationTests()
    {
        Environment.SetEnvironmentVariable(SoftPrintStateLocation.ConfigRootVariable, null);
        Environment.SetEnvironmentVariable(SoftPrintStateLocation.DataRootVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SoftPrintStateLocation.ConfigRootVariable, _previousConfig);
        Environment.SetEnvironmentVariable(SoftPrintStateLocation.DataRootVariable, _previousData);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* ignore */ }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigRootArgument_PutsEveryLocalPathUnderTheRoot(bool equalsForm)
    {
        var root = Path.Combine(_root, "x");
        var args = equalsForm
            ? new[] { "--config-root=" + root, "--headless" }
            : new[] { "--config-root", root, "--headless" };

        SoftPrintStateLocation.ApplyConfigRootArgument(args);

        Assert.True(SoftPrintStateLocation.IsIsolated);
        foreach (var (name, path) in ComputedPaths())
            AssertUnder(root, path, name);
    }

    [Fact]
    public void DataRootOnly_PutsLocalStateUnderTheDataRoot()
    {
        var data = Path.Combine(_root, "data-only");
        Environment.SetEnvironmentVariable(SoftPrintStateLocation.DataRootVariable, data);

        Assert.True(SoftPrintStateLocation.IsIsolated);
        Assert.Equal(Path.GetFullPath(data), UserAppPaths.ResolveDataRoot());
        AssertUnder(data, SoftPrintInstallLocation.LocalStateRoot, "LocalStateRoot");
        AssertUnder(data, PendingUpdateFinalizer.MarkerPath, "pending-update.json");
        AssertUnder(data, LocalUpdateHistoryStore.FilePath, "update-history.json");
        AssertUnder(data, SoftPrintUpdateApplier.WorkRoot, "update-work");
        AssertUnder(data, SoftPrintStateLocation.TempRoot, "temp");
    }

    [Fact]
    public void ExplicitRoot_SkipsLegacyMigrationFromOutsideTheRoot()
    {
        // Pasta do "executável" com dados de uma versão portátil antiga: com raiz explícita
        // nada disso pode vazar para dentro da raiz isolada.
        var contentRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(contentRoot, "data"));
        File.WriteAllText(Path.Combine(contentRoot, "data", "jobs.json"), "[\"legacy\"]");
        var root = Path.Combine(_root, "x");
        SoftPrintStateLocation.ApplyConfigRootArgument(["--config-root", root]);

        var paths = new UserAppPaths(new PlatformTests.TestHostEnvironment(contentRoot));

        Assert.Equal(Path.GetFullPath(root), paths.ConfigRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "data"), paths.DataRoot);
        Assert.False(File.Exists(Path.Combine(paths.DataRoot, "jobs.json")));
    }

    [Fact]
    public void UpdateStateRoundTrip_WritesOnlyUnderTheRoot()
    {
        var root = Path.Combine(_root, "x");
        SoftPrintStateLocation.ApplyConfigRootArgument(["--config-root", root, "--headless"]);
        var fullRoot = Path.GetFullPath(root);

        // O processo que dispara o update marca; o reaberto pelo script finaliza — mesmo caminho.
        var history = new LocalUpdateHistoryStore();
        PendingUpdateFinalizer.MarkPending(SoftPrintVersion.Current, rollback: false);
        Assert.True(File.Exists(Path.Combine(fullRoot, "pending-update.json")));
        PendingUpdateFinalizer.TryFinalize(history);
        Assert.False(File.Exists(Path.Combine(fullRoot, "pending-update.json")));
        Assert.True(history.Get().LastApplyOk);
        Assert.True(File.Exists(Path.Combine(fullRoot, "update-history.json")));

        UpdateFailureNotice.Record("falhou");
        Assert.True(File.Exists(Path.Combine(fullRoot, "last-update-error.txt")));
        Assert.Contains("falhou", UpdateFailureNotice.TryConsume(), StringComparison.Ordinal);

        var logs = new FileLogProvider(
            new UserAppPaths(new PlatformTests.TestHostEnvironment(Path.Combine(_root, "install"))),
            Options.Create(new SoftPrintFeatureOptions()));
        logs.CreateLogger("isolation").LogInformation("ok");

        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
            AssertUnder(fullRoot, file, file);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(fullRoot, "data", "logs")));
    }

    [Fact]
    public void WithoutExplicitRoot_PathsStayInLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftPrint");

        Assert.False(SoftPrintStateLocation.IsIsolated);
        Assert.Equal(expected, SoftPrintInstallLocation.LocalStateRoot);
        Assert.Equal(Path.Combine(expected, "pending-update.json"), PendingUpdateFinalizer.MarkerPath);
        Assert.Equal(Path.Combine(expected, "update-history.json"), LocalUpdateHistoryStore.FilePath);
        Assert.Equal(Path.Combine(expected, "update-work"), SoftPrintUpdateApplier.WorkRoot);
        Assert.Equal(Path.Combine(expected, "WebView2"), SoftPrintStateLocation.WebView2Folder);
        Assert.Equal(Path.GetTempPath(), SoftPrintStateLocation.TempRoot);
        Assert.Contains(UpdateFailureNotice.TempErrorPath, UpdateFailureNotice.PendingNoticePaths);
    }

    [Fact]
    public void WindowsService_KeepsUpdateWorkInItsOwnProfile()
    {
        // O serviço (LocalSystem) recebe --config-root com a pasta do usuário, mas executa os
        // scripts do update: eles não podem ficar numa pasta que o usuário grava.
        const string systemProfile = @"C:\Windows\System32\config\systemprofile\AppData\Local\SoftPrint";
        const string userRoot = @"C:\Users\fulano\AppData\Local\SoftPrint";

        Assert.Equal(systemProfile,
            SoftPrintStateLocation.ResolveLocalStateRoot(userRoot, null, windowsService: true, systemProfile));
        Assert.False(SoftPrintStateLocation.IsIsolatedFor(userRoot, null, windowsService: true));
        Assert.Equal(userRoot,
            SoftPrintStateLocation.ResolveLocalStateRoot(userRoot, null, windowsService: false, systemProfile));
        Assert.Equal(systemProfile,
            SoftPrintStateLocation.ResolveLocalStateRoot(null, null, windowsService: false, systemProfile));
    }

    private static IEnumerable<(string Name, string Path)> ComputedPaths()
    {
        yield return ("ConfigRoot", UserAppPaths.ResolveConfigRoot());
        yield return ("DataRoot", UserAppPaths.ResolveDataRoot());
        yield return ("LocalStateRoot", SoftPrintInstallLocation.LocalStateRoot);
        yield return ("pending-update.json", PendingUpdateFinalizer.MarkerPath);
        yield return ("update-history.json", LocalUpdateHistoryStore.FilePath);
        yield return ("update-work", SoftPrintUpdateApplier.WorkRoot);
        yield return ("previous", LocalPreviousVersionStore.PreviousDirectory);
        yield return ("version.txt", LocalPreviousVersionStore.VersionMarkerPath);
        yield return ("update-script-error.txt", UpdateFailureNotice.ScriptErrorPath);
        yield return ("last-update-error.txt", UpdateFailureNotice.PersistentErrorPath);
        yield return ("WebView2", SoftPrintStateLocation.WebView2Folder);
        yield return ("temp", SoftPrintStateLocation.TempRoot);
        foreach (var notice in UpdateFailureNotice.PendingNoticePaths)
            yield return ("aviso de update", notice);
    }

    private static void AssertUnder(string root, string path, string name)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.True(
            fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase),
            $"{name} fora da raiz: {path} (raiz {root})");
    }
}
