using System.Drawing;
using System.Drawing.Printing;
using System.Security.Cryptography;
using System.Text.Json;

var instanceName = "Local\\AutoPrint-" + Convert.ToHexString(SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..24];
using var instance = new Mutex(true, instanceName, out var firstInstance);
if (!firstInstance)
{
    if (!args.Contains("--headless")) MessageBox.Show("O AutoPrint desta pasta já está aberto. Use o painel existente na barra de tarefas.", "AutoPrint");
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddHostedService<PrintWorker>();
var app = builder.Build();
var store = app.Services.GetRequiredService<JobStore>();
var settings = app.Services.GetRequiredService<SettingsStore>();
var headless = builder.Configuration.GetValue<bool>("headless");
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (headless) return;
    var address = app.Urls.First();
    var thread = new Thread(() =>
    {
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var panel = new Dashboard(address, store.ApiKey);
        panel.FormClosed += (_, _) => app.Lifetime.StopApplication();
        using var registration = app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (panel.IsHandleCreated && !panel.IsDisposed)
                try { panel.BeginInvoke(() => panel.Close()); } catch (InvalidOperationException) { }
        });
        System.Windows.Forms.Application.Run(panel);
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
});

app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    if (!CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(context.Request.Headers["X-AutoPrint-Key"].ToString()),
        System.Text.Encoding.UTF8.GetBytes(store.ApiKey)))
    {
        context.Response.StatusCode = 401;
        return;
    }
    await next();
});

app.MapGet("/api/status", () =>
{
    var current = settings.Current;
    return new { application = "AutoPrint", simulation = current.Simulation, printer = current.PrinterName, settings = current };
});
app.MapGet("/api/settings", () => settings.Current);
app.MapPut("/api/settings", (SettingsRequest request) =>
{
    var printer = request.PrinterName?.Trim() ?? "";
    if ((!request.Simulation && string.IsNullOrWhiteSpace(printer)) ||
        (!string.IsNullOrWhiteSpace(printer) && !PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer)))
        return Results.BadRequest(new { error = "Selecione uma impressora instalada no Windows. Atualize a lista e tente novamente." });
    try { return Results.Ok(settings.Update(request with { PrinterName = printer })); }
    catch (SettingsConflictException) { return Results.Conflict(new { error = "As configurações mudaram. Recarregue antes de salvar." }); }
});
app.MapGet("/api/printers", () => PrinterSettings.InstalledPrinters.Cast<string>().ToArray());
app.MapGet("/api/jobs", () => store.Snapshot());
app.MapGet("/api/jobs/{id:guid}", (Guid id) =>
    store.Snapshot().FirstOrDefault(j => j.Id == id) is { } job ? (IResult)Results.Ok(job) : Results.NotFound());
app.MapPost("/api/jobs", (SubmitJob request) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 50000)
        return Results.BadRequest(new { error = "Envie texto entre 1 e 50.000 caracteres." });
    if (string.IsNullOrWhiteSpace(request.Reference) || request.Reference.Length > 120)
        return Results.BadRequest(new { error = "Informe uma referência única de até 120 caracteres." });
    var result = store.Add(request);
    return Results.Accepted($"/api/jobs/{result.Id}", result);
});
try { app.Run(); }
catch (Exception exception)
{
    if (!headless) MessageBox.Show("Não foi possível iniciar o AutoPrint. Verifique se ele já está aberto.\n\n" + exception.Message,
        "AutoPrint", MessageBoxButtons.OK, MessageBoxIcon.Error);
    throw;
}

record SubmitJob(string Reference, string Text);
record PrintJob(Guid Id, string Reference, string Text, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null, string? Error = null, string? PrinterName = null, long? SettingsRevision = null);

sealed class JobStore
{
    private readonly object gate = new();
    private readonly string path;
    private List<PrintJob> jobs;
    public string ApiKey { get; }

    public JobStore(IHostEnvironment environment)
    {
        var directory = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "jobs.json");
        var keyPath = Path.Combine(directory, "api-key.txt");
        if (!File.Exists(keyPath)) File.WriteAllText(keyPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        ApiKey = File.ReadAllText(keyPath).Trim();
        if (ApiKey.Length < 32) throw new InvalidOperationException("Chave de API inválida.");
        jobs = File.Exists(path)
            ? JsonSerializer.Deserialize<List<PrintJob>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Fila inválida.")
            : new();
        // Uma interrupção pode ocorrer após o envio: exigir conferência evita duplicatas.
        jobs = jobs.Select(j => j.Status == "processing"
            ? j with { Status = "uncertain", Error = "Aplicativo interrompido durante o envio. Confira a impressora antes de reenviar." }
            : j).ToList();
        Save();
    }

    public PrintJob[] Snapshot() { lock (gate) return jobs.ToArray(); }
    public PrintJob Add(SubmitJob request)
    {
        lock (gate)
        {
            var existing = jobs.FirstOrDefault(j => j.Reference == request.Reference);
            if (existing is not null) return existing;
            var job = new PrintJob(Guid.NewGuid(), request.Reference, request.Text, "pending", DateTimeOffset.UtcNow);
            jobs.Add(job);
            try { Save(); } catch { jobs.Remove(job); throw; }
            return job;
        }
    }
    public PrintJob? Take(PrinterOptions options)
    {
        lock (gate)
        {
            var index = jobs.FindIndex(j => j.Status == "pending");
            if (index < 0) return null;
            var original = jobs[index];
            jobs[index] = original with { Status = "processing", PrinterName = options.PrinterName, SettingsRevision = options.Revision };
            try { Save(); } catch { jobs[index] = original; throw; }
            return jobs[index];
        }
    }
    public void Finish(Guid id, string status, string? error = null)
    {
        lock (gate)
        {
            var index = jobs.FindIndex(j => j.Id == id);
            jobs[index] = jobs[index] with { Status = status, Error = error, FinishedAt = DateTimeOffset.UtcNow };
            Save();
        }
    }
    private void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

sealed class PrintWorker(JobStore store, SettingsStore settings, ILogger<PrintWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Devolve o controle para permitir a inicialização do servidor.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = settings.Current;
            if (options.Paused) { await Task.Delay(500, stoppingToken); continue; }
            var job = store.Take(options);
            if (job is null) { await Task.Delay(500, stoppingToken); continue; }
            string status;
            string? error = null;
            try
            {
                if (options.Simulation) status = "simulated";
                else { Print(job, options.PrinterName); status = "sent"; }
            }
            catch (Exception exception)
            {
                status = "uncertain";
                error = exception.Message;
                logger.LogError(exception, "Falha ao enviar trabalho {JobId}", job.Id);
            }
            // Falha de persistência encerra o serviço; não continuar enviando sem registrar.
            store.Finish(job.Id, status, error);
        }
    }

    private void Print(PrintJob job, string printer)
    {
        if (string.IsNullOrWhiteSpace(printer)) throw new InvalidOperationException("Configure o nome exato da impressora.");
        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = printer;
        if (!document.PrinterSettings.IsValid) throw new InvalidOperationException("Impressora não encontrada no Windows.");
        document.DocumentName = $"AutoPrint {job.Reference}";
        document.PrintController = new StandardPrintController();
        using var font = new Font("Consolas", 10);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.LineLimit };
        var remaining = job.Text;
        document.PrintPage += (_, page) =>
        {
            var graphics = page.Graphics ?? throw new InvalidOperationException("Impressora sem área gráfica.");
            var area = page.MarginBounds;
            if (area.Width <= 0 || area.Height <= 0) throw new InvalidOperationException("Área de impressão inválida. Confira papel e margens no Windows.");
            graphics.MeasureString(remaining, font, area.Size, format, out var count, out var linesFilled);
            if (count <= 0) throw new InvalidOperationException("Não foi possível ajustar o texto ao papel.");
            graphics.DrawString(remaining[..count], font, Brushes.Black, area, format);
            remaining = remaining[count..];
            page.HasMorePages = remaining.Length > 0;
        };
        document.Print();
    }
}
