using SoftPrint.Domain;

namespace SoftPrint.UI;

/// <summary>Feedback visual enquanto a API e o painel sobem — evita cliques repetidos no atalho.</summary>
public sealed class SplashForm : Form
{
    private readonly Label _status;
    private readonly ProgressBar _bar;

    public SplashForm()
    {
        var version = SoftPrintVersion.Current;
        Text = $"SoftPrint v{version}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 240);
        BackColor = Color.FromArgb(15, 23, 32);
        ForeColor = Color.FromArgb(238, 243, 247);
        ShowInTaskbar = true;
        TopMost = true;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

        var brand = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 72,
            TextAlign = ContentAlignment.BottomCenter,
            Font = new Font("Segoe UI", 28f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            Text = "SoftPrint",
            Padding = new Padding(0, 16, 0, 0)
        };

        var versionLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(154, 171, 188),
            Text = $"versão {version}"
        };

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(238, 243, 247),
            Text = "Abrindo o SoftPrint…"
        };

        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
            Height = 14,
            Dock = DockStyle.Top,
            Margin = new Padding(40, 8, 40, 8)
        };

        var barHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(40, 12, 40, 8)
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
            Text = "Aguarde — não é preciso clicar de novo."
        };

        Controls.Add(tip);
        Controls.Add(barHost);
        Controls.Add(_status);
        Controls.Add(versionLabel);
        Controls.Add(brand);
    }

    public void SetStatus(string message)
    {
        if (IsDisposed || !_status.IsHandleCreated)
        {
            _status.Text = message;
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed) _status.Text = message;
            });
        }
        catch (InvalidOperationException)
        {
            _status.Text = message;
        }
    }

    public void CloseSafe()
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(Close);
            else Close();
        }
        catch (InvalidOperationException)
        {
            try { Close(); } catch { /* ignore */ }
        }
    }
}
