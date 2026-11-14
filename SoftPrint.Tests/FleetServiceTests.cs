using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
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

    [Fact]
    public async Task Heartbeat_ReadsPendingCommands_AndSendsPaused()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"status":"success","data":{"ok":true,"pending_commands":2}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        var result = await fleet.SendHeartbeatAsync();

        Assert.Equal(2, result.PendingCommands);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(body.RootElement.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task GetCommands_ParsesList_WithMachineId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        handler.ByPath["/api/softprint/agent/commands"] = (HttpStatusCode.OK,
            """{"status":"success","data":[{"id":7,"type":"pause","requested_by":"ana"},{"id":8,"type":"refresh","requested_by":""}]}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        var cmds = await fleet.GetCommandsAsync();

        Assert.Equal(new[] { 7L, 8L }, cmds.Select(c => c.Id));
        Assert.Equal("pause", cmds[0].Type);
        Assert.Equal("ana", cmds[0].RequestedBy);
        Assert.Contains("machineId=" + fleet.GetStatus().MachineId, handler.Requests[0].Url);
    }

    [Fact]
    public async Task SendCommandResult_PostsOkAndMessage()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"status":"success","data":{}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        Assert.True(await fleet.SendCommandResultAsync(7, true, "Fila pausada"));

        var req = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/softprint/agent/commands/7/result", req.Url);
        using var body = JsonDocument.Parse(req.Body);
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Fila pausada", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(fleet.GetStatus().MachineId, body.RootElement.GetProperty("machineId").GetString());
    }

    [Fact]
    public async Task RealtimeTicket_ParsesUrlTokenAndExpiry_OrNullWhenUnavailable()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        handler.ByPath["/api/softprint/agent/realtime-token"] = (HttpStatusCode.OK,
            """{"status":"success","data":{"ws_url":"wss://hub.test/v1/ws","token":"jwt","room":"softprint:m:3","expires_at":"2026-11-15T10:00:00+00:00"}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        var ticket = await fleet.GetRealtimeTicketAsync();

        Assert.NotNull(ticket);
        Assert.Equal(new Uri("wss://hub.test/v1/ws"), ticket!.WsUrl);
        Assert.Equal("softprint:m:3", ticket.Room);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero), ticket.ExpiresAt);

        handler.ByPath["/api/softprint/agent/realtime-token"] = (HttpStatusCode.ServiceUnavailable, """{"message":"off"}""");
        Assert.Null(await fleet.GetRealtimeTicketAsync());
    }

    [Fact]
    public void PendingUpdate_SurvivesRestart_UntilCleared()
    {
        var fleet = Create(new RecordingHandler(HttpStatusCode.OK, "{}"));
        fleet.RememberPendingUpdate(42);

        var again = Create(new RecordingHandler(HttpStatusCode.OK, "{}"));
        var pending = again.GetPendingUpdate();
        Assert.Equal(42, pending!.CommandId);
        Assert.Equal(SoftPrintVersion.Current, pending.FromVersion);

        again.ClearPendingUpdate();
        Assert.Null(Create(new RecordingHandler(HttpStatusCode.OK, "{}")).GetPendingUpdate());
    }

    [Fact]
    public void TokenChanged_IsCancelled_WhenTokenIsSet()
    {
        var fleet = Create(new RecordingHandler(HttpStatusCode.OK, "{}"));
        var before = fleet.TokenChanged;

        fleet.SetToken("sp_1_x000000000000");

        Assert.True(before.IsCancellationRequested);
        Assert.False(fleet.TokenChanged.IsCancellationRequested);
    }

    [Fact]
    public async Task Heartbeat_NullPendingCommands_IsOkWithZero()
    {
        var fleet = Create(new RecordingHandler(HttpStatusCode.OK,
            """{"status":"success","data":{"ok":true,"pending_commands":null}}"""));
        fleet.SetToken("sp_1_x000000000000");

        var result = await fleet.SendHeartbeatAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, result.PendingCommands);
    }

    [Fact]
    public async Task Heartbeat_SendsPausedTrue_WhenSettingsArePaused()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var fleet = Create(handler, settings: new PausedSettings());
        fleet.SetToken("sp_1_x000000000000");

        await fleet.SendHeartbeatAsync();

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.True(body.RootElement.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task GetCommands_SkipsMalformedItems_KeepsValidOnes()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        handler.ByPath["/api/softprint/agent/commands"] = (HttpStatusCode.OK,
            """{"data":[{"id":1,"type":"pause"},{"id":"x","type":"pause"},"lixo",{"id":3,"type":5},{"id":2,"type":"resume"}]}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        var cmds = await fleet.GetCommandsAsync();

        Assert.Equal(new[] { 1L, 2L }, cmds.Select(c => c.Id));
    }

    [Fact]
    public async Task GetCommands_NetworkFailure_ReturnsEmpty()
    {
        var fleet = Create(new ThrowingHandler());
        fleet.SetToken("sp_1_x000000000000");

        Assert.Empty(await fleet.GetCommandsAsync());
    }

    [Fact]
    public async Task RealtimeTicket_RejectsNonWebSocketUrl()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        handler.ByPath["/api/softprint/agent/realtime-token"] = (HttpStatusCode.OK,
            """{"data":{"ws_url":"http://x/v1/ws","token":"jwt","room":"r","expires_at":"2026-11-15T10:00:00+00:00"}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        Assert.Null(await fleet.GetRealtimeTicketAsync());
    }

    [Fact]
    public void TokenChanged_NotCancelled_WhenSameTokenSavedAgain()
    {
        var fleet = Create(new RecordingHandler(HttpStatusCode.OK, "{}"));
        fleet.SetToken("sp_1_x000000000000");
        var current = fleet.TokenChanged;

        fleet.SetToken(" sp_1_x000000000000 ");

        Assert.False(current.IsCancellationRequested);
    }

    private FleetService Create(HttpMessageHandler handler, SoftPrintFeatureOptions? options = null, ISettingsRepository? settings = null)
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
            NullLogger<FleetService>.Instance,
            settings);
    }

    private sealed class PausedSettings : ISettingsRepository
    {
        public PrintOptions Current { get; } = new() { Paused = true };

        public PrintOptions Update(string printerName, bool simulation, bool paused, ImageFitMode imageFit,
            int imageScalePercent, PaperSizeKind paperSize, double paperWidthMm, double paperHeightMm,
            bool paperLandscape, long expectedRevision) => throw new NotSupportedException();

        public PrintOptions UpdateInboxEntries(IReadOnlyList<InboxEntry> entries, long expectedRevision) =>
            throw new NotSupportedException();
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
        public Dictionary<string, (HttpStatusCode Status, string Body)> ByPath { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-SoftPrint-Token", out var t) ? t.FirstOrDefault() : null,
                request.Content?.Headers.ContentType?.ToString() ?? "",
                content));
            var (s, b) = ByPath.TryGetValue(request.RequestUri!.AbsolutePath, out var r) ? r : (status, body);
            return new HttpResponseMessage(s) { Content = new StringContent(b) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Nenhuma conexão pôde ser feita");
    }
}
