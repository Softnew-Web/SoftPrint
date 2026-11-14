using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.Infrastructure.Persistence;
using Xunit;

namespace SoftPrint.Tests;

public sealed class FleetServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softprint-fleet-" + Guid.NewGuid().ToString("N"));

    public FleetServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task WithoutToken_SendsNothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var fleet = Create(handler);

        var result = await fleet.SendHeartbeatAsync();

        Assert.False(result.Ok);
        Assert.False(fleet.GetStatus().Configured);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Heartbeat_SendsTokenMachineAndQueue_AndReadsEmpresa()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"status":"success","data":{"ok":true,"status":"online","empresa":"Clinica Teste"}}""");
        var fleet = Create(handler, new SoftPrintFeatureOptions { FleetUrl = "https://backend.test/" });
        fleet.SetToken("  sp_7_abcdef0123456789  ");

        var result = await fleet.SendHeartbeatAsync();

        Assert.True(result.Ok);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://backend.test/api/softprint/agent/heartbeat", req.Url);
        Assert.Equal("sp_7_abcdef0123456789", req.Token);
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("heartbeat", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal(fleet.GetStatus().MachineId, body.RootElement.GetProperty("machineId").GetString());
        Assert.Equal(Environment.MachineName, body.RootElement.GetProperty("hostname").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("pending").GetInt32());

        var status = fleet.GetStatus();
        Assert.True(status.LastOk);
        Assert.Equal("Clinica Teste", status.Empresa);
        Assert.Equal("sp_7_a…6789", status.TokenHint);
    }

    [Fact]
    public async Task RejectedToken_ReportsClearMessage()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized,
            """{"status":"error","message":"Token do SoftPrint inválido.","code":401}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_errado000000");

        var result = await fleet.SendHeartbeatAsync();

        Assert.False(result.Ok);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("Token recusado", result.Message);
        Assert.False(fleet.GetStatus().LastOk);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotThrow()
    {
        var fleet = Create(new ThrowingHandler());
        fleet.SetToken("sp_1_qualquer00000");

        var result = await fleet.SendHeartbeatAsync();

        Assert.False(result.Ok);
        Assert.Contains("Sem conexão", result.Message);
    }

    [Fact]
    public void MachineId_PersistsAcrossInstances_AndTokenFallsBackToEnv()
    {
        var options = new SoftPrintFeatureOptions { FleetToken = "sp_9_doenv000000000" };
        var first = Create(new RecordingHandler(HttpStatusCode.OK, "{}"), options);
        var second = Create(new RecordingHandler(HttpStatusCode.OK, "{}"), options);

        Assert.Equal(first.GetStatus().MachineId, second.GetStatus().MachineId);
        Assert.True(second.GetStatus().Configured);
        Assert.True(second.GetStatus().TokenFromEnv);

        second.SetToken("sp_9_datela0000000");
        var third = Create(new RecordingHandler(HttpStatusCode.OK, "{}"), options);
        Assert.False(third.GetStatus().TokenFromEnv);
        Assert.Equal("sp_9_d…0000", third.GetStatus().TokenHint);
    }

    [Fact]
    public async Task SupportPackage_IsSentAsMultipart_AndOversizeIsRefused()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"status":"success","data":{"id":1}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_pacote000000");

        var zip = Path.Combine(_root, "softprint-support-teste.zip");
        File.WriteAllBytes(zip, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        var ok = await fleet.UploadSupportPackageAsync(zip, "impressora travou");

        Assert.True(ok.Ok);
        var req = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/softprint/agent/support-packages", req.Url);
        Assert.Contains("multipart/form-data", req.ContentType);
        Assert.Contains("softprint-support-teste.zip", req.Body);
        Assert.Contains("impressora travou", req.Body);

        var big = Path.Combine(_root, "grande.zip");
        using (var fs = File.Create(big)) fs.SetLength(FleetService.MaxPackageBytes + 1);
        var refused = await fleet.UploadSupportPackageAsync(big, null);
        Assert.False(refused.Ok);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChangeToken_AnnouncesStoppingWithOldToken_ThenSwitches()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_antigo0000000");

        await fleet.ChangeTokenAsync("sp_2_novo000000000");

        var req = Assert.Single(handler.Requests);
        Assert.Equal("sp_1_antigo0000000", req.Token);
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("stopping", body.RootElement.GetProperty("kind").GetString());

        await fleet.SendHeartbeatAsync();
        Assert.Equal("sp_2_novo000000000", handler.Requests[^1].Token);
    }

    [Fact]
    public async Task ChangeToken_ClearingToken_AnnouncesStopping_AndSameTokenSendsNothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_mesmo00000000");

        await fleet.ChangeTokenAsync(" sp_1_mesmo00000000 ");
        Assert.Empty(handler.Requests);

        await fleet.ChangeTokenAsync("");
        var req = Assert.Single(handler.Requests);
        Assert.Equal("sp_1_mesmo00000000", req.Token);
        Assert.False(fleet.GetStatus().Configured);
    }

    [Fact]
    public async Task ChangeToken_StillSwitches_WhenServerIsUnreachable()
    {
        var fleet = Create(new ThrowingHandler());
        fleet.SetToken("sp_1_antigo0000000");

        await fleet.ChangeTokenAsync("sp_2_novo000000000");

        Assert.Equal("sp_2_n…0000", fleet.GetStatus().TokenHint);
    }

    [Theory]
    [InlineData(true, "6.1.7601", true)]   // Windows 7 SP1
    [InlineData(true, "6.2.9200", true)]   // Windows 8
    [InlineData(true, "6.3.9600", false)]  // Windows 8.1
    [InlineData(true, "10.0.19045", false)]
    [InlineData(false, "5.15.0", false)]   // Linux
    public void LegacyTls_OnlyForWindowsBefore81(bool isWindows, string version, bool expected) =>
        Assert.Equal(expected,
            SoftPrint.Infrastructure.Hosting.SoftPrintHostExtensions.NeedsExplicitTls12(isWindows, Version.Parse(version)));

    private FleetService Create(HttpMessageHandler handler, SoftPrintFeatureOptions? options = null)
    {
        var paths = new TestPaths(_root);
        var queue = new JobQueueService(
            new JsonJobRepository(paths),
            new PrintJobFactory(),
            new TemplateRenderer(Options.Create(new SoftPrintFeatureOptions())));
        return new FleetService(
            new SingleClientFactory(handler),
            paths,
            Options.Create(options ?? new SoftPrintFeatureOptions()),
            new PlatformCapabilities("windows", "winspool", true, true, "registry", false),
            queue,
            NullLogger<FleetService>.Instance);
    }

    private sealed record TestPaths(string Root) : IAppPaths
    {
        public string DataRoot => Path.Combine(Root, "data");
        public string ConfigRoot => Path.Combine(Root, "config");
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Captured(string Url, string? Token, string ContentType, string Body);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-SoftPrint-Token", out var t) ? t.FirstOrDefault() : null,
                request.Content?.Headers.ContentType?.ToString() ?? "",
                content));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Nenhuma conexão pôde ser feita");
    }
}
