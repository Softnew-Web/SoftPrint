using Microsoft.Extensions.Logging;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Updates;

namespace SoftPrint.Infrastructure.Integrations;

/// <summary>Quem dispara a busca de comandos (WebSocket e heartbeat).</summary>
public interface IFleetCommandPoller
{
    Task<int> PollAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Executa os comandos do painel: um por vez, cada id uma vez só. Lista fechada de tipos.
/// O resultado do "update" sai depois do reinício (<see cref="ReportPendingUpdateAsync"/>).
/// </summary>
public sealed class FleetCommandRunner(
    IFleetService fleet,
    IUpdateApplier updates,
    SettingsService settings,
    JobQueueService jobs,
    ISupportBundleService support,
    ILogger<FleetCommandRunner> log) : IFleetCommandPoller
{
    private const int RememberIds = 200;
    private const int MaxReprint = 50;
    private const int MaxMessage = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<long> _doneOrder = new();
    private readonly HashSet<long> _done = new();

    private sealed record Outcome(bool Ok, string Message);

    public async Task<int> PollAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var commands = await fleet.GetCommandsAsync(cancellationToken).ConfigureAwait(false);
            var ran = 0;
            foreach (var command in commands)
            {
                if (!Remember(command.Id)) continue;
                fleet.NoteCommand(command.Type);
                var outcome = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (outcome is not null)
                    await fleet.SendCommandResultAsync(command.Id, outcome.Ok, Clip(outcome.Message), cancellationToken)
                        .ConfigureAwait(false);
                ran++;
            }
            return ran;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "Falha ao buscar comandos do monitoramento");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Depois do reinício: diz ao painel se a atualização pedida por ele funcionou.</summary>
    public async Task ReportPendingUpdateAsync(CancellationToken cancellationToken = default)
    {
        var pending = fleet.GetPendingUpdate();
        if (pending is null) return;

        var current = SoftPrintVersion.Current;
        var ok = !string.Equals(
            SoftPrintVersionCompare.Normalize(pending.FromVersion),
            SoftPrintVersionCompare.Normalize(current),
            StringComparison.OrdinalIgnoreCase);
        var message = ok
            ? $"Atualizado de {pending.FromVersion} para {current}."
            : "A atualização não foi concluída. " +
              (UpdateFailureNotice.Peek() ?? $"O SoftPrint continua na versão {current}.");

        if (await fleet.SendCommandResultAsync(pending.CommandId, ok, Clip(message), cancellationToken).ConfigureAwait(false))
        {
            try { fleet.ClearPendingUpdate(); }
            catch (Exception ex) { log.LogWarning(ex, "Não foi possível limpar o pedido de atualização pendente"); }
        }
    }

    private async Task<Outcome?> ExecuteAsync(FleetCommand command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.Type)
            {
                case "update":
                    try { fleet.RememberPendingUpdate(command.Id); }
                    catch (Exception ex) { return new Outcome(false, "Não foi possível guardar o pedido de atualização: " + ex.Message); }
                    if (!updates.TryStart(out var error))
                    {
                        fleet.ClearPendingUpdate();
                        return new Outcome(false, error ?? "Não foi possível iniciar a atualização.");
                    }
                    return null; // resultado depois do reinício
                case "pause":
                    SetPaused(true);
                    return new Outcome(true, "Fila pausada.");
                case "resume":
                    SetPaused(false);
                    return new Outcome(true, "Fila retomada.");
                case "reprint_failed":
                    return ReprintFailed();
                case "test_print":
                    var job = GuidedTestPrint.Submit(jobs, settings, command.RequestedBy ?? "painel Softnew");
                    return new Outcome(true, $"Impressão de teste na fila ({job.Reference}).");
                case "support_package":
                    var path = support.CreateBundle();
                    var note = "Pedido pelo painel" + (command.RequestedBy is null ? "" : " por " + command.RequestedBy);
                    var sent = await fleet.UploadSupportPackageAsync(path, note, cancellationToken).ConfigureAwait(false);
                    return new Outcome(sent.Ok, sent.Message);
                case "refresh":
                    var hb = await fleet.SendHeartbeatAsync("heartbeat", cancellationToken).ConfigureAwait(false);
                    return new Outcome(hb.Ok, hb.Ok ? "Status enviado." : hb.Message);
                default:
                    return new Outcome(false, "Comando desconhecido: " + command.Type);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "Comando {Type} ({Id}) falhou", command.Type, command.Id);
            return new Outcome(false, ex.Message);
        }
    }

    private void SetPaused(bool paused)
    {
        for (var attempt = 0; ; attempt++)
        {
            var c = settings.Current;
            if (c.Paused == paused) return;
            try
            {
                settings.Update(c.PrinterName, c.Simulation, paused, c.ImageFit.ToWire(), c.ImageScalePercent,
                    c.PaperSize.ToWire(), c.PaperWidthMm, c.PaperHeightMm, c.PaperLandscape, c.Revision);
                return;
            }
            catch (SettingsConflictException) when (attempt == 0)
            {
                // Alguém salvou ao mesmo tempo: tenta de novo com a revisão nova.
            }
        }
    }

    private Outcome ReprintFailed()
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var failed = jobs.List()
            .Where(j => j.Status == JobStatus.Uncertain && (j.FinishedAt ?? j.CreatedAt) >= since)
            .OrderBy(j => j.FinishedAt ?? j.CreatedAt)
            .Take(MaxReprint)
            .ToList();
        var ok = 0;
        foreach (var job in failed)
        {
            try
            {
                jobs.Reprint(job.Id);
                ok++;
            }
            catch (ArgumentException ex)
            {
                log.LogDebug(ex, "Reimpressão de {Id} recusada", job.Id);
            }
        }
        return new Outcome(true, $"{ok} trabalho(s) reenviado(s).");
    }

    private bool Remember(long id)
    {
        lock (_done)
        {
            if (!_done.Add(id)) return false;
            _doneOrder.Enqueue(id);
            while (_doneOrder.Count > RememberIds) _done.Remove(_doneOrder.Dequeue());
            return true;
        }
    }

    private static string Clip(string message) =>
        message.Length <= MaxMessage ? message : message[..MaxMessage];
}
