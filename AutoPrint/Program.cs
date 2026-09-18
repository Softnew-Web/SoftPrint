using AutoPrint.Api.Endpoints;
using AutoPrint.Composition;

using var instance = SingleInstanceGuard.TryAcquire(out var firstInstance);
if (!firstInstance)
{
    if (!args.Contains("--headless"))
        MessageBox.Show("O AutoPrint desta pasta já está aberto. Use o painel existente na barra de tarefas.", "AutoPrint");
    return;
}

var builder = ApplicationComposer.CreateBuilder(args);
var app = ApplicationComposer.BuildApplication(builder);
var headless = builder.Configuration.GetValue<bool>("headless");

ApplicationComposer.StartDashboardIfNeeded(app, headless);
app.MapWebDashboard();
app.MapJobEndpoints();
app.MapSettingsEndpoints();

try
{
    app.Run();
}
catch (Exception exception)
{
    if (!headless)
        MessageBox.Show(
            "Não foi possível iniciar o AutoPrint. Verifique se ele já está aberto.\n\n" + exception.Message,
            "AutoPrint", MessageBoxButtons.OK, MessageBoxIcon.Error);
    throw;
}
