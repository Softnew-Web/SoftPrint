using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutoPrint.UI;

/// <summary>Host WinForms fino: a UI real é HTML/CSS (Tailwind) no WebView2.</summary>
public sealed class Dashboard : Form
{
    private readonly string _address;
    private readonly NotifyIcon? _tray;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public Dashboard(string address, string apiKey, Application.AutoPrintFeatureOptions features, NotifyIcon? tray = null)
    {
        _ = apiKey;
        _ = features;
        _address = address.TrimEnd('/');
        _tray = tray;

        Text = "AutoPrint";
        ClientSize = new Size(1400, 920);
        MinimumSize = new Size(1200, 760);
        StartPosition = FormStartPosition.CenterScreen;
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

    private async Task InitAsync()
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoPrint",
                "WebView2");
            Directory.CreateDirectory(userData);
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

            // Cache-bust para sempre puxar o dashboard/JS novos.
            _web.CoreWebView2.Navigate($"{_address}/dashboard?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "Não foi possível carregar o painel WebView2.\nInstale o Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "AutoPrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
