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
    private const string RestartMessage = "O SoftPrint vai reiniciar para atualizar; envie o comando de novo depois.";

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
            var restarting = false;
            foreach (var command in commands)
            {
                if (!Remember(command.Id)) continue;
                if (restarting)
                {
                    await SendResultAsync(command.Id, false, RestartMessage, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                fleet.NoteCommand(command.Type);
                var outcome = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (outcome is not null)
                    await SendResultAsync(command.Id, outcome.Ok, outcome.Message, cancellationToken).ConfigureAwait(false);
                else if (command.Type == "update")
                    restarting = true;
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReportPendingUpdateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReportPendingUpdateCoreAsync(CancellationToken cancellationToken)
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

        if (await SendResultAsync(pending.CommandId, ok, message, cancellationToken).ConfigureAwait(false))
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
                    if (fleet.GetPendingUpdate() is not null || updates.GetStatus().InProgress)
                        return new Outcome(false, "Já existe uma atualização em andamento.");
                    try { fleet.RememberPendingUpdate(command.Id); }
                    catch (Exception ex) { return new Outcome(false, "Não foi possível guardar o pedido de atualização: " + ex.Message); }
                    if (!updates.TryStart(out var error))
                    {
                        try { fleet.ClearPendingUpdate(); }
                        catch (Exception clearEx) { log.LogWarning(clearEx, "Não foi possível desfazer o pedido de atualização pendente"); }
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
                    try
                    {
                        var note = "Pedido pelo painel" + (command.RequestedBy is null ? "" : " por " + command.RequestedBy);
                        var sent = await fleet.UploadSupportPackageAsync(path, note, cancellationToken).ConfigureAwait(false);
                        return new Outcome(sent.Ok, sent.Message);
                    }
                    finally
                    {
                        try { File.Delete(path); } catch { /* ignore */ }
                    }
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
        var all = jobs.List();
        var reprinted = all.Where(j => j.ReprintedFromId is Guid).Select(j => j.ReprintedFromId!.Value).ToHashSet();
        var failed = all
            .Where(j => j.Status == JobStatus.Uncertain && !reprinted.Contains(j.Id)
                        && (j.FinishedAt ?? j.CreatedAt) >= since)
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

    /// <summary>Envia o resultado; se o servidor não receber, tenta mais uma vez depois de 2 s.</summary>
    private async Task<bool> SendResultAsync(long id, bool ok, string message, CancellationToken cancellationToken)
    {
        var clipped = Clip(message);
        if (await fleet.SendCommandResultAsync(id, ok, clipped, cancellationToken).ConfigureAwait(false)) return true;
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        if (await fleet.SendCommandResultAsync(id, ok, clipped, cancellationToken).ConfigureAwait(false)) return true;
        log.LogWarning("Resultado do comando {Id} não chegou ao servidor", id);
        return false;
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
