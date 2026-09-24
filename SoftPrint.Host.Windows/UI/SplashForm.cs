using SoftPrint.Domain;

namespace SoftPrint.UI;

/// <summary>Tela de abertura com barra de progresso (mínimo ~15s; pode alongar se houver atualização).</summary>
public sealed class SplashForm : Form
{
    private static readonly (int UntilPercent, string Status, string Detail)[] Stages =
    {
        (8, "Iniciando SoftPrint…", "Preparando o ambiente…"),
        (18, "Carregando configurações…", "Lendo preferências e impressora…"),
        (28, "Preparando impressão…", "Carregando estratégias e fila…"),
        (40, "Buscando versões…", "Consultando o servidor de atualizações…"),
        (52, "Conferindo atualizações…", "Comparando com a versão instalada…"),
        (64, "Iniciando monitoramento…", "Conectando à caixa de entrada…"),
        (76, "Montando o painel…", "Carregando a interface web…"),
        (88, "Organizando arquivos…", "Preparando logs e histórico…"),
        (100, "Quase pronto…", "Finalizando a abertura…")
    };

    private readonly Label _status;
    private readonly Label _detail;
    private readonly ProgressBar _bar;
    private readonly Label _percent;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _resumeTimer;
    private DateTime _startedUtc;
    private readonly int _durationMs;
    private bool _started;
    private string? _liveStatus;
    private string? _liveDetail;
    private int? _livePercent;
    private bool _lockProgress;

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
            Text = Stages[0].Status
        };

        _detail = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = Color.FromArgb(154, 171, 188),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Text = Stages[0].Detail
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
        _resumeTimer = new System.Windows.Forms.Timer { Interval = 2_200 };
        _resumeTimer.Tick += (_, _) =>
        {
            _resumeTimer.Stop();
            ResumeStages();
        };
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
    /// Progresso real (atualização / etapa pontual).
    /// Com <paramref name="resumeAfterMs"/> &gt; 0, a mensagem some e as etapas automáticas voltam.
    /// </summary>
    public void SetLiveProgress(
        int? percent,
        string? status,
        string? detail = null,
        int resumeAfterMs = 0,
        bool lockProgress = false)
    {
        void Apply()
        {
            if (IsDisposed) return;
            _resumeTimer.Stop();
            if (status is not null) _liveStatus = status;
            if (detail is not null) _liveDetail = detail;
            if (percent is int p)
                _livePercent = Math.Clamp(p, 0, 100);
            if (lockProgress)
                _lockProgress = true;

            if (_liveStatus is not null) _status.Text = _liveStatus;
            if (_liveDetail is not null) _detail.Text = _liveDetail;

            if ((IsFinished || _lockProgress) && _livePercent is int live)
                ApplyBar(Math.Max(_bar.Value, live));

            if (!_timer.Enabled && !IsDisposed)
                _timer.Start();

            if (resumeAfterMs > 0 && !_lockProgress)
            {
                _resumeTimer.Interval = Math.Clamp(resumeAfterMs, 800, 8_000);
                _resumeTimer.Start();
            }
        }

        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(Apply); }
            catch (InvalidOperationException) { Apply(); }
        }
        else Apply();
    }

    /// <summary>Volta a narrar as etapas automáticas de abertura (config, painel, etc.).</summary>
    public void ResumeStages()
    {
        void Apply()
        {
            if (IsDisposed || _lockProgress) return;
            _resumeTimer.Stop();
            _liveStatus = null;
            _liveDetail = null;
            if (!IsFinished)
            {
                var stage = StageFor(_bar.Value);
                _status.Text = stage.Status;
                _detail.Text = stage.Detail;
            }
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
            _resumeTimer.Stop();
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
        _resumeTimer.Stop();
        _timer.Dispose();
        _resumeTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void TickProgress()
    {
        if (IsDisposed) return;

        if (!IsFinished && !_lockProgress)
        {
            var elapsed = (DateTime.UtcNow - _startedUtc).TotalMilliseconds;
            var raw = Math.Clamp(elapsed / _durationMs, 0, 1);
            var value = (int)Math.Round(raw * 100);
            ApplyBar(Math.Max(_bar.Value, value));

            if (_liveStatus is null)
            {
                var stage = StageFor(_bar.Value);
                _status.Text = stage.Status;
                _detail.Text = stage.Detail;
            }
            else
            {
                _status.Text = _liveStatus;
                if (_liveDetail is not null) _detail.Text = _liveDetail;
            }

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

        // Atualização em curso (ou após os 15s): acompanha o progresso real.
        if (_livePercent is int live)
        {
            ApplyBar(Math.Max(_bar.Value, live));
            if (_liveStatus is not null) _status.Text = _liveStatus;
            if (_liveDetail is not null) _detail.Text = _liveDetail;
        }
        else if (IsFinished && _liveStatus is null)
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

    private static (string Status, string Detail) StageFor(int percent)
    {
        foreach (var (until, status, detail) in Stages)
        {
            if (percent <= until)
                return (status, detail);
        }
        return ("Quase pronto…", "Finalizando a abertura…");
    }
}
