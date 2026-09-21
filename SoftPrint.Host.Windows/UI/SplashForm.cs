using SoftPrint.Domain;

namespace SoftPrint.UI;

/// <summary>Tela de abertura com barra de progresso (efeito visual ~15s).</summary>
public sealed class SplashForm : Form
{
    private static readonly (int UntilPercent, string Message)[] Stages =
    {
        (8, "Preparando SoftPrint…"),
        (22, "Baixando componentes…"),
        (38, "Baixando interface do painel…"),
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
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly int _durationMs;

    public bool IsFinished { get; private set; }

    public SplashForm(int durationMs = 15_000)
    {
        _durationMs = Math.Clamp(durationMs, 3_000, 60_000);
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
            Text = "Preparando download dos arquivos…"
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
        Shown += (_, _) =>
        {
            if (!_timer.Enabled && !IsFinished)
                _timer.Start();
        };
    }

    public void SetStatus(string message)
    {
        // Durante a animação fake, a barra controla o texto principal.
        if (_timer.Enabled) return;
        SetLabel(_status, message);
    }

    public void WaitUntilFinished()
    {
        if (IsFinished || IsDisposed) return;
        if (!_timer.Enabled)
            _timer.Start();

        while (!IsFinished && !IsDisposed)
        {
            System.Windows.Forms.Application.DoEvents();
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
        var elapsed = (DateTime.UtcNow - _startedUtc).TotalMilliseconds;
        var raw = Math.Clamp(elapsed / _durationMs, 0, 1);
        // Curva suave: sobe rápido no começo e desacelera no fim.
        var eased = 1 - Math.Pow(1 - raw, 1.65);
        var value = (int)Math.Round(eased * 100);
        if (value < _bar.Value) value = _bar.Value;
        _bar.Value = Math.Min(100, value);
        _percent.Text = $"{_bar.Value}%";

        foreach (var (until, message) in Stages)
        {
            if (_bar.Value <= until)
            {
                _status.Text = message;
                break;
            }
        }

        _detail.Text = _bar.Value switch
        {
            < 15 => "Conectando…",
            < 35 => "Recebendo pacote 1 de 4…",
            < 55 => "Recebendo pacote 2 de 4…",
            < 75 => "Recebendo pacote 3 de 4…",
            < 95 => "Recebendo pacote 4 de 4…",
            _ => "Finalizando…"
        };

        if (raw >= 1 || _bar.Value >= 100)
        {
            _bar.Value = 100;
            _percent.Text = "100%";
            _status.Text = "Pronto!";
            _detail.Text = "Abrindo o painel…";
            _timer.Stop();
            IsFinished = true;
        }
    }

    private void SetLabel(Label label, string message)
    {
        if (IsDisposed || !label.IsHandleCreated)
        {
            label.Text = message;
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed) label.Text = message;
            });
        }
        catch (InvalidOperationException)
        {
            label.Text = message;
        }
    }
}
