using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Integrations;

namespace SoftPrint.UI;

/// <summary>Host WinForms fino: a UI real é HTML/CSS (Tailwind) no WebView2.</summary>
public sealed class Dashboard : Form
{
    private readonly string _address;
    private readonly TrayAppNotifier? _notifier;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _exitRequested;
    private bool _hideBalloonShown;

    public Dashboard(
        string address,
        string apiKey,
        Application.SoftPrintFeatureOptions features,
        TrayAppNotifier? notifier = null)
    {
        _ = apiKey;
        _ = features;
        _address = address.TrimEnd('/');
        _notifier = notifier;

        Text = $"SoftPrint v{SoftPrintVersion.Current}";
        ClientSize = new Size(1400, 920);
        MinimumSize = new Size(1200, 760);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        BackColor = Color.FromArgb(15, 23, 32);
        Controls.Add(_web);
        UiHost.MainForm = this;
        Shown += async (_, _) => await InitAsync();
        FormClosed += (_, _) =>
        {
            if (ReferenceEquals(UiHost.MainForm, this))
                UiHost.MainForm = null;
            try { _web.Dispose(); } catch { /* ignore */ }
        };
    }

    public static bool HasWebView2Runtime()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    public bool ExitRequested => _exitRequested;

    public void HideToTray(bool balloon = true)
    {
        ShowInTaskbar = false;
        Hide();
        if (!balloon || _notifier is null || _hideBalloonShown) return;
        _hideBalloonShown = true;
        _notifier.ShowBalloonTip(
            4000,
            "SoftPrint",
            "Continua imprimindo em segundo plano. Clique duas vezes no ícone da bandeja para abrir o painel.",
            ToolTipIcon.Info);
    }

    public void ShowFromTray()
    {
        if (IsDisposed) return;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Maximized;
        Activate();
        BringToFront();
    }

    public void RequestExit()
    {
        _exitRequested = true;
        if (IsDisposed) return;
        if (IsHandleCreated)
        {
            try { BeginInvoke(Close); }
            catch (InvalidOperationException) { Close(); }
        }
        else Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X / Alt+F4 → bandeja. Encerrar de verdade só com Sair (RequestExit).
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
            HideToTray(balloon: false);
    }

    private async Task InitAsync()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, "SoftPrint", "WebView2");
            var legacy = Path.Combine(local, "AutoPrint", "WebView2");
            Directory.CreateDirectory(userData);
            if (!Directory.EnumerateFileSystemEntries(userData).Any() && Directory.Exists(legacy))
            {
                try { CopyDirectory(legacy, userData); }
                catch { /* profile antigo permanece como fallback */ }
            }

            if (!HasWebView2Runtime())
            {
                OpenInBrowser("Microsoft Edge WebView2 Runtime não encontrado.");
                return;
            }

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            try
            {
                await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache |
                    CoreWebView2BrowsingDataKinds.CacheStorage);
            }
            catch
            {
                /* ignore cache clear failures */
            }

            _web.CoreWebView2.Navigate($"{_address}/dashboard?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        }
        catch (Exception ex)
        {
            OpenInBrowser(ex.Message);
        }
    }

    private void OpenInBrowser(string reason)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{_address}/dashboard")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            /* ignore */
        }

        _web.Visible = false;
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12),
            Text = "Painel aberto no navegador.\nO SoftPrint continua em execução na bandeja.\n\n" + reason
        });
        _notifier?.ShowBalloonTip(6000, "SoftPrint", "Painel aberto no navegador. A impressão segue em segundo plano.", ToolTipIcon.Info);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination);
            if (!File.Exists(target))
                File.Copy(file, target);
        }
    }
}
