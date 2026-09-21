using SoftPrint.Domain;

namespace SoftPrint.UI;

/// <summary>Tela de abertura com barra de progresso (mínimo ~15s; pode alongar se houver atualização).</summary>
public sealed class SplashForm : Form
{
    private static readonly (int UntilPercent, string Message)[] Stages =
    {
        (8, "Preparando SoftPrint…"),
        (22, "Verificando atualizações…"),
        (38, "Baixando componentes…"),
        (55, "Baixando módulos de impressão…"),
        (72, "Verificando arquivos locais…"),
        (88, "Carregando painel…"),
        (100, "Quase pronto…")
    };

    private readonly Label _status;
    private readonly Label _detail;
    private readonly ProgressBar _bar;
    private readonly Label _percent;
    private readonly System.Windows.Forms.Timer _timer;
    private DateTime _startedUtc;
    private readonly int _durationMs;
    private bool _started;
    private string? _liveStatus;
    private string? _liveDetail;
    private int? _livePercent;

    public bool IsFinished { get; private set; }

    public SplashForm(int durationMs = 15_000)
    {
        _durationMs = Math.Clamp(durationMs, 5_000, 60_000);
        _startedUtc = DateTime.UtcNow;
        var version = SoftPrintVersion.Current;
        Text = $"SoftPrint v{version}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 280);
        BackColor = Color.FromArgb(15, 23, 32);
        ForeColor = Color.FromArgb(238, 243, 247);
        ShowInTaskbar = true;
        TopMost = true;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

        var brand = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 68,
            TextAlign = ContentAlignment.BottomCenter,
            Font = new Font("Segoe UI", 28f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            Text = "SoftPrint",
            Padding = new Padding(0, 14, 0, 0)
        };

        var versionLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(154, 171, 188),
            Text = $"versão {version}"
        };

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(238, 243, 247),
            Text = "Abrindo o SoftPrint…"
        };

        _detail = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = Color.FromArgb(154, 171, 188),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Text = "Preparando…"
        };

        _percent = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(94, 234, 212),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "0%"
        };

        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 16
        };

        var barHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(40, 10, 40, 10)
        };
        _bar.Dock = DockStyle.Fill;
        barHost.Controls.Add(_bar);

        var tip = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = Color.FromArgb(154, 171, 188),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Text = "Aguarde — não é preciso clicar de novo.",
            Padding = new Padding(16, 4, 16, 0)
        };

        Controls.Add(tip);
        Controls.Add(barHost);
        Controls.Add(_percent);
        Controls.Add(_detail);
        Controls.Add(_status);
        Controls.Add(versionLabel);
        Controls.Add(brand);

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => TickProgress();
        Shown += (_, _) => StartProgress();
    }

    public void StartProgress()
    {
        if (_started || IsDisposed) return;
        _started = true;
        _startedUtc = DateTime.UtcNow;
        IsFinished = false;
        _bar.Value = 0;
        _percent.Text = "0%";
        if (!_timer.Enabled)
            _timer.Start();
    }

    /// <summary>
    /// Progresso real (atualização). Antes dos 15s mínimos só o texto muda;
    /// depois a barra acompanha o download.
    /// </summary>
    public void SetLiveProgress(int? percent, string? status, string? detail = null)
    {
        void Apply()
        {
            if (IsDisposed) return;
            if (status is not null) _liveStatus = status;
            if (detail is not null) _liveDetail = detail;
            if (percent is int p)
                _livePercent = Math.Clamp(p, 0, 100);

            if (_liveStatus is not null) _status.Text = _liveStatus;
            if (_liveDetail is not null) _detail.Text = _liveDetail;

            if (IsFinished && _livePercent is int live)
                ApplyBar(Math.Max(_bar.Value, live));

            if (!_timer.Enabled && !IsDisposed)
                _timer.Start();
        }

        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(Apply); }
            catch (InvalidOperationException) { Apply(); }
        }
        else Apply();
    }

    public void WaitUntilFinished() => WaitUntilReady(() => false);

    /// <summary>
    /// Espera o tempo mínimo (~15s) e permanece aberto enquanto
    /// <paramref name="keepWaiting"/> for true (ex.: baixando atualização).
    /// </summary>
    public void WaitUntilReady(Func<bool> keepWaiting, Action? onTick = null)
    {
        if (IsDisposed) return;
        StartProgress();

        while (!IsDisposed)
        {
            try { onTick?.Invoke(); }
            catch { /* ignore UI tick errors */ }

            System.Windows.Forms.Application.DoEvents();

            var busy = false;
            try { busy = keepWaiting(); }
            catch { busy = false; }

            if (IsFinished && !busy)
                break;

            Thread.Sleep(20);
        }
    }

    public void CloseSafe()
    {
        if (IsDisposed) return;
        try
        {
            _timer.Stop();
            if (InvokeRequired) BeginInvoke(Close);
            else Close();
        }
        catch (InvalidOperationException)
        {
            try { Close(); } catch { /* ignore */ }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void TickProgress()
    {
        if (IsDisposed) return;

        if (!IsFinished)
        {
            var elapsed = (DateTime.UtcNow - _startedUtc).TotalMilliseconds;
            var raw = Math.Clamp(elapsed / _durationMs, 0, 1);
            var value = (int)Math.Round(raw * 100);
            ApplyBar(Math.Max(_bar.Value, value));

            _status.Text = _liveStatus ?? StageMessage(_bar.Value);
            _detail.Text = _liveDetail ?? DetailMessage(_bar.Value);

            if (raw >= 1)
            {
                IsFinished = true;
                if (_livePercent is null && _liveStatus is null)
                {
                    ApplyBar(100);
                    _status.Text = "Pronto!";
                    _detail.Text = "Abrindo o painel…";
                    _timer.Stop();
                }
            }
            return;
        }

        // Depois dos 15s: só acompanha atualização em curso.
        if (_livePercent is int live)
        {
            ApplyBar(Math.Max(_bar.Value, live));
            if (_liveStatus is not null) _status.Text = _liveStatus;
            if (_liveDetail is not null) _detail.Text = _liveDetail;
        }
        else
        {
            _timer.Stop();
        }
    }

    private void ApplyBar(int value)
    {
        value = Math.Clamp(value, 0, 100);
        _bar.Value = value;
        _percent.Text = $"{value}%";
    }

    private static string StageMessage(int percent)
    {
        foreach (var (until, message) in Stages)
        {
            if (percent <= until)
                return message;
        }
        return "Quase pronto…";
    }

    private static string DetailMessage(int percent) => percent switch
    {
        < 15 => "Conectando…",
        < 35 => "Consultando versões…",
        < 55 => "Recebendo pacote…",
        < 75 => "Validando arquivos…",
        < 95 => "Quase lá…",
        _ => "Finalizando…"
    };
}
